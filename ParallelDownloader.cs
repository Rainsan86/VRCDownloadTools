using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace VRCDownloadTool
{
    /// <summary>
    /// 多线程（分段）下载器。
    ///
    /// 先发送一个只取 1 字节的 Range 探测请求：
    ///  - 服务器返回 206，则把文件等分成若干段并行下载，每段直接写入文件对应偏移，全程不占内存；
    ///  - 服务器返回 200，说明它忽略了 Range，该响应本身就是完整文件，直接保留即可；
    ///  - 其它情况（不支持分段、认证失败、分段被截断、写盘失败等）一律报告失败，
    ///    由调用方回退到原来的单线程下载方式。
    ///
    /// 网络类错误会自动重试，尽量不触发回退。失败时一定会删除自己写出的半成品文件，
    /// 让回退路径从一个干净的状态开始。
    /// </summary>
    public sealed class ParallelDownloader
    {
        /// <summary>未指定连接数时使用的默认值。</summary>
        public static int DefaultMaxConnections = 8;

        /// <summary>单个连接最少负责多少数据，小于它两倍的文件不会分段。</summary>
        public static long DefaultMinimumChunkSize = 8L * 1024 * 1024;

        /// <summary>未指定重试次数时使用的默认值（单个分段最多额外重试这么多次）。</summary>
        public static int DefaultRetriesPerPart = 5;

        private const int MaxConnectionsLimit = 32;
        private const int MaxRetriesLimit = 30;
        private const int MaxProbeAttempts = 4;
        private const int PartTimeoutSeconds = 60 * 60;
        private const int ProbeTimeoutSeconds = 120;
        private const float ProgressInterval = 0.1f;
        private const int StreamBufferSize = 1 << 16;

        private const string LogPrefix = "[VRC下载工具] ";

        private enum Phase
        {
            Idle,
            Probing,
            Parts,
            Done
        }

        public string Url { get; private set; }
        public string OutputFile { get; private set; }

        /// <summary>分段下载使用的连接数，会被限制在 1~32。</summary>
        public int MaxConnections { get; set; }

        /// <summary>单个分段的最小大小，至少为 1 字节。</summary>
        public long MinimumChunkSize { get; set; }

        /// <summary>单个分段失败后额外重试的次数，会被限制在 0~30。</summary>
        public int RetriesPerPart { get; set; }

        /// <summary>是否为用户主动取消。</summary>
        public bool WasCancelled { get; private set; }

        /// <summary>
        /// 每个请求发出前都会调用一次，用来附加认证信息
        /// （VRChat 那边传入的是 VRC.Core.API 的公开方法），SDK 本身不做任何修改。
        /// </summary>
        public Action<UnityWebRequest> ConfigureRequest { get; set; }

        public bool IsRunning { get { return _phase == Phase.Probing || _phase == Phase.Parts; } }

        private Phase _phase = Phase.Idle;

        private UnityWebRequest _probeRequest;
        private ChunkHandler _probeHandler;
        private FileStream _probeStream;
        private int _probeAttempts;

        private UnityWebRequest[] _partRequests;
        private ChunkHandler[] _partHandlers;
        private FileStream[] _partStreams;
        private long[] _partStarts;
        private long[] _partEnds;
        private long[] _partWritten;
        private bool[] _partComplete;
        private int[] _partRetries;
        private int _partCount;

        private long _totalSize = -1;
        private long _probeTotalSize = -1;
        private bool _stopped;
        private Action<float> _onProgress;
        private Action<bool, string> _onFinished;
        private float _lastProgressTime;

        public ParallelDownloader(string url, string outputFile)
        {
            Url = url;
            OutputFile = outputFile;
            MaxConnections = DefaultMaxConnections;
            MinimumChunkSize = DefaultMinimumChunkSize;
            RetriesPerPart = DefaultRetriesPerPart;
        }

        /// <summary>
        /// 开始下载。<paramref name="onProgress"/> 收到 0~100 的进度，
        /// <paramref name="onFinished"/> 只会被调用一次。
        /// </summary>
        public void Start(Action<float> onProgress, Action<bool, string> onFinished)
        {
            if (_phase != Phase.Idle)
                throw new InvalidOperationException("ParallelDownloader 只能启动一次。");

            _onProgress = onProgress;
            _onFinished = onFinished;

            if (string.IsNullOrEmpty(Url))
            {
                Finish(false, "下载地址为空。");
                return;
            }

            if (string.IsNullOrEmpty(OutputFile))
            {
                Finish(false, "输出文件路径为空。");
                return;
            }

            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(OutputFile));
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
            }
            catch (Exception exception)
            {
                Finish(false, "无法创建输出目录：" + exception.Message);
                return;
            }

            string streamError;
            if (!TryOpenProbeStream(out streamError))
            {
                Finish(false, streamError);
                return;
            }

            _phase = Phase.Probing;
            SendProbe();
        }

        /// <summary>取消正在进行的下载，结束回调会以失败结果触发。</summary>
        public void Abort()
        {
            if (_phase != Phase.Probing && _phase != Phase.Parts)
                return;

            WasCancelled = true;
            CancelDownload();
        }

        // ------------------------------------------------------------------ 探测

        private bool TryOpenProbeStream(out string error)
        {
            error = null;
            try
            {
                _probeStream = new FileStream(OutputFile, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite, StreamBufferSize);
                return true;
            }
            catch (Exception exception)
            {
                error = "无法打开输出文件：" + exception.Message;
                return false;
            }
        }

        private void SendProbe()
        {
            _probeAttempts++;

            UnityWebRequest request = null;
            try
            {
                request = new UnityWebRequest(Url, UnityWebRequest.kHttpVerbGET);
                request.timeout = ProbeTimeoutSeconds;
                if (ConfigureRequest != null)
                    ConfigureRequest(request);
                request.SetRequestHeader("Range", "bytes=0-0");

                ChunkHandler handler = new ChunkHandler(_probeStream, request, long.MaxValue, true);
                handler.OnData = OnProbeData;
                request.downloadHandler = handler;

                _probeRequest = request;
                _probeHandler = handler;

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                operation.completed += OnProbeCompleted;
            }
            catch (Exception exception)
            {
                DisposeRequest(request);
                HandleProbeFailure("启动探测请求失败：" + exception.Message, true);
            }
        }

        private void OnProbeData()
        {
            if (_probeTotalSize <= 0 && _probeRequest != null)
            {
                long start;
                long end;
                long total;
                if (TryParseContentRange(_probeRequest.GetResponseHeader("Content-Range"), out start, out end, out total) && total > 0)
                    _probeTotalSize = total;
                else
                    _probeTotalSize = GetContentLength(_probeRequest);
            }

            ChunkHandler handler = _probeHandler;
            if (handler != null)
                ReportProgress(handler.Written, _probeTotalSize, false);
        }

        private void OnProbeCompleted(AsyncOperation operation)
        {
            UnityWebRequest request = _probeRequest;
            ChunkHandler handler = _probeHandler;
            if (request == null && handler == null)
                return;

            _probeRequest = null;
            _probeHandler = null;

            long responseCode = request != null ? request.responseCode : 0;
            string requestError = request != null ? request.error : null;
            string contentRangeHeader = request != null ? request.GetResponseHeader("Content-Range") : null;
            long contentLength = GetContentLength(request);
            long written = handler != null ? handler.Written : 0;
            string handlerError = handler != null ? handler.Error : null;
            bool handlerRetryable = handler == null || handler.Retryable;

            CloseProbeStream();
            DisposeRequest(request);

            if (_stopped)
                return;

            if (!string.IsNullOrEmpty(handlerError))
            {
                HandleProbeFailure(handlerError, handlerRetryable);
                return;
            }

            if (!string.IsNullOrEmpty(requestError))
            {
                // 只有完全没连上（responseCode 为 0）或服务端 5xx 才值得重试。
                bool retryable = responseCode == 0 || responseCode >= 500;
                HandleProbeFailure("探测请求失败：" + requestError, retryable);
                return;
            }

            if (responseCode == 200)
            {
                // 服务器忽略了 Range，直接返回了完整文件，而我们已经把它写到磁盘上了。
                if (contentLength > 0 && contentLength != written)
                {
                    Fail("下载内容不完整（收到 " + written + " 字节，预期 " + contentLength + " 字节）。");
                    return;
                }

                _totalSize = written;
                ReportProgress(written, written, true);
                Succeed();
                return;
            }

            if (responseCode != 206)
            {
                bool retryable = responseCode == 0 || responseCode >= 500;
                HandleProbeFailure("服务器未按分段请求返回内容（HTTP " + responseCode + "）。", retryable);
                return;
            }

            long rangeStart;
            long rangeEnd;
            long rangeTotal;
            if (!TryParseContentRange(contentRangeHeader, out rangeStart, out rangeEnd, out rangeTotal) || rangeTotal <= 0)
            {
                HandleProbeFailure("服务器没有返回分段请求的文件总大小。", true);
                return;
            }

            if (rangeStart != 0)
            {
                HandleProbeFailure("服务器返回了意料之外的字节区间（起始位置 " + rangeStart + "）。", true);
                return;
            }

            _totalSize = rangeTotal;
            _probeTotalSize = rangeTotal;

            if (_totalSize == 0)
            {
                Succeed();
                return;
            }

            StartParts();
        }

        private void HandleProbeFailure(string reason, bool retryable)
        {
            if (_stopped)
                return;

            if (retryable && _probeAttempts < MaxProbeAttempts)
            {
                Debug.LogWarning(LogPrefix + "探测失败，正在重试（第 " + _probeAttempts + " 次）：" + reason);

                string streamError;
                if (!TryOpenProbeStream(out streamError))
                {
                    Fail(streamError);
                    return;
                }

                _probeTotalSize = -1;
                SendProbe();
                return;
            }

            Fail(reason);
        }

        // ------------------------------------------------------------------ 分段

        private void StartParts()
        {
            long size = _totalSize;
            int connections = Mathf.Clamp(MaxConnections, 1, MaxConnectionsLimit);
            long minimumChunk = MinimumChunkSize > 0 ? MinimumChunkSize : DefaultMinimumChunkSize;

            long partsBySize = size / minimumChunk;
            if (partsBySize < 1)
                partsBySize = 1;

            int parts = (int)Math.Min((long)connections, partsBySize);
            if (parts > size)
                parts = (int)size;
            if (parts < 1)
                parts = 1;

            _partCount = parts;
            _partRequests = new UnityWebRequest[parts];
            _partHandlers = new ChunkHandler[parts];
            _partStreams = new FileStream[parts];
            _partStarts = new long[parts];
            _partEnds = new long[parts];
            _partWritten = new long[parts];
            _partComplete = new bool[parts];
            _partRetries = new int[parts];

            // 按顺序切分，保证所有区间首尾相接、既无重叠也无空洞。
            long baseSize = size / parts;
            long remainder = size % parts;
            long offset = 0;
            for (int i = 0; i < parts; i++)
            {
                long length = baseSize + (i < remainder ? 1 : 0);
                _partStarts[i] = offset;
                _partEnds[i] = offset + length - 1;
                offset += length;
            }

            try
            {
                using (FileStream stream = new FileStream(OutputFile, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite, StreamBufferSize))
                {
                    stream.SetLength(size);
                }
            }
            catch (Exception exception)
            {
                Fail("无法初始化输出文件：" + exception.Message);
                return;
            }

            _phase = Phase.Parts;
            for (int i = 0; i < parts; i++)
            {
                StartPart(i);
                if (_stopped)
                    return;
            }
        }

        private void StartPart(int index)
        {
            try
            {
                ClosePartStream(index);

                FileStream stream = new FileStream(OutputFile, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite, StreamBufferSize);
                stream.Seek(_partStarts[index], SeekOrigin.Begin);
                _partStreams[index] = stream;
                _partWritten[index] = 0;
                _partComplete[index] = false;

                UnityWebRequest request = new UnityWebRequest(Url, UnityWebRequest.kHttpVerbGET);
                request.timeout = PartTimeoutSeconds;
                if (ConfigureRequest != null)
                    ConfigureRequest(request);
                request.SetRequestHeader("Range",
                    "bytes=" + _partStarts[index].ToString(CultureInfo.InvariantCulture) +
                    "-" + _partEnds[index].ToString(CultureInfo.InvariantCulture));

                long expected = _partEnds[index] - _partStarts[index] + 1;
                ChunkHandler handler = new ChunkHandler(stream, request, expected, false);
                handler.OnData = OnPartData;
                request.downloadHandler = handler;

                _partRequests[index] = request;
                _partHandlers[index] = handler;

                int capturedIndex = index;
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                operation.completed += delegate { OnPartCompleted(capturedIndex); };
            }
            catch (Exception exception)
            {
                HandlePartFailure(index, "无法建立第 " + (index + 1) + " 个连接：" + exception.Message, true);
            }
        }

        private void OnPartData()
        {
            long done = 0;
            if (_partWritten != null)
            {
                for (int i = 0; i < _partCount; i++)
                {
                    ChunkHandler handler = _partHandlers != null ? _partHandlers[i] : null;
                    done += handler != null ? handler.Written : _partWritten[i];
                }
            }

            ReportProgress(done, _totalSize, false);
        }

        private void OnPartCompleted(int index)
        {
            if (_partRequests == null || index < 0 || index >= _partRequests.Length)
                return;

            UnityWebRequest request = _partRequests[index];
            ChunkHandler handler = _partHandlers[index];
            if (request == null && handler == null)
                return;

            _partRequests[index] = null;
            _partHandlers[index] = null;

            long written = handler != null ? handler.Written : 0;
            long expected = _partEnds[index] - _partStarts[index] + 1;
            long responseCode = request != null ? request.responseCode : 0;
            string requestError = request != null ? request.error : null;
            string handlerError = handler != null ? handler.Error : null;
            bool handlerRetryable = handler == null || handler.Retryable;
            string contentRange = request != null ? request.GetResponseHeader("Content-Range") : null;

            ClosePartStream(index);
            DisposeRequest(request);

            if (_stopped)
                return;

            _partWritten[index] = written;

            string error = null;
            bool retryable = false;

            if (!string.IsNullOrEmpty(handlerError))
            {
                error = handlerError;
                retryable = handlerRetryable;
            }
            else if (!string.IsNullOrEmpty(requestError))
            {
                error = requestError;
                retryable = responseCode == 0 || responseCode >= 500;
            }
            else if (responseCode != 206)
            {
                error = "服务器对分段请求返回了 HTTP " + responseCode;
                retryable = responseCode >= 500;
            }
            else if (!ContentRangeMatchesStart(contentRange, _partStarts[index]))
            {
                error = "服务器返回的字节区间与请求不一致";
                retryable = true;
            }
            else if (written != expected)
            {
                error = "只收到 " + written + "/" + expected + " 字节";
                retryable = true;
            }

            ReportProgress(TotalBytesWritten(), _totalSize, true);

            if (error == null)
            {
                _partComplete[index] = true;
                if (AllPartsComplete())
                    Complete();
                return;
            }

            HandlePartFailure(index, error, retryable);
        }

        private void HandlePartFailure(int index, string reason, bool retryable)
        {
            if (_stopped)
                return;

            int maxRetries = Mathf.Clamp(RetriesPerPart, 0, MaxRetriesLimit);
            if (retryable && _partRetries[index] < maxRetries)
            {
                _partRetries[index]++;
                Debug.LogWarning(LogPrefix + "第 " + (index + 1) + "/" + _partCount + " 个分段下载失败，正在重试（第 " +
                                 _partRetries[index] + "/" + maxRetries + " 次）：" + reason);
                StartPart(index);
                return;
            }

            Fail("第 " + (index + 1) + "/" + _partCount + " 个分段下载失败：" + reason);
        }

        private bool AllPartsComplete()
        {
            if (_partComplete == null)
                return false;
            for (int i = 0; i < _partCount; i++)
            {
                if (!_partComplete[i])
                    return false;
            }
            return true;
        }

        private long TotalBytesWritten()
        {
            long total = 0;
            if (_partWritten == null)
                return 0;
            for (int i = 0; i < _partCount; i++)
            {
                if (_partWritten[i] > 0)
                    total += _partWritten[i];
            }
            return total;
        }

        private void Complete()
        {
            if (_stopped)
                return;

            DisposeParts(false);

            long actual = -1;
            try
            {
                actual = new FileInfo(OutputFile).Length;
            }
            catch (Exception)
            {
                // 下面会以文件大小不符的形式报错。
            }

            if (actual != _totalSize)
            {
                Fail("下载完成后文件大小为 " + actual + " 字节，预期 " + _totalSize + " 字节。");
                return;
            }

            ReportProgress(actual, actual, true);
            Succeed();
        }

        // ------------------------------------------------------------------ 生命周期

        private void Succeed()
        {
            if (_stopped)
                return;
            _stopped = true;
            Finish(true, null);
        }

        /// <summary>用户主动取消：清理现场，但不作为错误打日志。</summary>
        private void CancelDownload()
        {
            if (_stopped)
                return;
            _stopped = true;

            DisposeParts(true);
            DisposeProbe(true);
            DeleteOutputFile();
            Finish(false, "下载已取消。");
        }

        private void Fail(string reason)
        {
            if (_stopped)
                return;
            _stopped = true;

            Debug.LogWarning(LogPrefix + "多线程下载失败：" + reason);
            DisposeParts(true);
            DisposeProbe(true);
            DeleteOutputFile();
            Finish(false, reason);
        }

        private void Finish(bool success, string error)
        {
            _phase = Phase.Done;
            Action<bool, string> callback = _onFinished;
            _onFinished = null;
            _onProgress = null;
            if (callback == null)
                return;

            try
            {
                callback(success, error);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private void DisposeProbe(bool abort)
        {
            UnityWebRequest request = _probeRequest;
            _probeRequest = null;
            _probeHandler = null;

            if (abort && request != null)
            {
                try
                {
                    request.Abort();
                }
                catch (Exception)
                {
                }
            }

            DisposeRequest(request);
            CloseProbeStream();
        }

        private void DisposeParts(bool abort)
        {
            if (_partRequests != null)
            {
                for (int i = 0; i < _partRequests.Length; i++)
                {
                    UnityWebRequest request = _partRequests[i];
                    _partRequests[i] = null;
                    _partHandlers[i] = null;

                    if (abort && request != null)
                    {
                        try
                        {
                            request.Abort();
                        }
                        catch (Exception)
                        {
                        }
                    }

                    DisposeRequest(request);
                    ClosePartStream(i);
                }
            }
        }

        private void CloseProbeStream()
        {
            FileStream stream = _probeStream;
            _probeStream = null;
            CloseStream(stream);
        }

        private void ClosePartStream(int index)
        {
            if (_partStreams == null || index < 0 || index >= _partStreams.Length)
                return;

            FileStream stream = _partStreams[index];
            _partStreams[index] = null;
            CloseStream(stream);
        }

        private static void CloseStream(FileStream stream)
        {
            if (stream == null)
                return;
            try
            {
                stream.Flush();
            }
            catch (Exception)
            {
            }
            try
            {
                stream.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private static void DisposeRequest(UnityWebRequest request)
        {
            if (request == null)
                return;
            try
            {
                request.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private void DeleteOutputFile()
        {
            try
            {
                if (File.Exists(OutputFile))
                    File.Delete(OutputFile);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(LogPrefix + "无法删除未完成的下载文件：" + exception.Message);
            }
        }

        // ------------------------------------------------------------------ 进度

        private void ReportProgress(long done, long total, bool force)
        {
            if (_onProgress == null || total <= 0)
                return;
            if (!force && Time.realtimeSinceStartup - _lastProgressTime < ProgressInterval)
                return;

            _lastProgressTime = Time.realtimeSinceStartup;
            float percent = Mathf.Clamp01((float)((double)done / (double)total)) * 100f;
            try
            {
                _onProgress(percent);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        // ------------------------------------------------------------------ 工具方法

        private static long GetContentLength(UnityWebRequest request)
        {
            if (request == null)
                return -1;
            return ParseLong(request.GetResponseHeader("Content-Length"));
        }

        private static long ParseLong(string value)
        {
            long parsed;
            if (string.IsNullOrEmpty(value))
                return -1;
            if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return -1;
            return parsed;
        }

        /// <summary>解析 "bytes start-end/total" 形式的响应头。</summary>
        private static bool TryParseContentRange(string value, out long start, out long end, out long total)
        {
            start = -1;
            end = -1;
            total = -1;

            if (string.IsNullOrEmpty(value))
                return false;

            int slash = value.IndexOf('/');
            if (slash < 0)
                return false;

            string range = value.Substring(0, slash).Trim();
            string totalPart = value.Substring(slash + 1).Trim();

            int dash = range.LastIndexOf('-');
            if (dash < 0)
                return false;

            string startPart = range.Substring(0, dash).Trim();
            int space = startPart.LastIndexOf(' ');
            if (space >= 0)
                startPart = startPart.Substring(space + 1).Trim();

            if (!long.TryParse(startPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out start))
                return false;
            if (!long.TryParse(range.Substring(dash + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
                return false;

            if (totalPart != "*")
            {
                long parsedTotal;
                if (long.TryParse(totalPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedTotal))
                    total = parsedTotal;
            }

            return true;
        }

        private static bool ContentRangeMatchesStart(string contentRange, long expectedStart)
        {
            // 有些服务器不返回该响应头；那时响应码和字节数已经校验过，只有互相矛盾的头才算错误。
            if (string.IsNullOrEmpty(contentRange))
                return true;

            long start;
            long end;
            long total;
            if (!TryParseContentRange(contentRange, out start, out end, out total))
                return true;

            return start == expectedStart;
        }

        // ------------------------------------------------------------------ 响应处理器

        /// <summary>
        /// 把单个连接的响应体直接写入它在磁盘上的字节区间。
        /// 一旦收到的数据超过请求范围就立刻终止，这样即使服务器忽略了 Range 头也不会写坏文件。
        /// </summary>
        private sealed class ChunkHandler : DownloadHandlerScript
        {
            private readonly FileStream _stream;
            private readonly UnityWebRequest _request;
            private readonly long _maxBytes;
            private readonly bool _allowUnrangedResponse;

            private long _written;
            private string _error;
            private bool _encodingChecked;

            public Action OnData;

            public long Written { get { return _written; } }
            public string Error { get { return _error; } }

            /// <summary>该错误是否值得重试。</summary>
            public bool Retryable { get; private set; }

            public ChunkHandler(FileStream stream, UnityWebRequest request, long maxBytes, bool allowUnrangedResponse)
            {
                _stream = stream;
                _request = request;
                _maxBytes = maxBytes;
                _allowUnrangedResponse = allowUnrangedResponse;
                Retryable = true;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0)
                    return true;

                if (_error != null)
                    return false;

                if (!_encodingChecked)
                {
                    _encodingChecked = true;
                    string encoding = _request != null ? _request.GetResponseHeader("Content-Encoding") : null;
                    if (!string.IsNullOrEmpty(encoding) &&
                        !encoding.Trim().Equals("identity", StringComparison.OrdinalIgnoreCase))
                    {
                        // 字节偏移只在原始数据上才对得上，压缩过的响应无法按分段拼装。
                        _error = "服务器对响应做了压缩（" + encoding + "），无法进行分段下载。";
                        Retryable = false;
                        return false;
                    }
                }

                long responseCode = _request != null ? _request.responseCode : 0;
                if (responseCode != 0 && responseCode != 206 && !(responseCode == 200 && _allowUnrangedResponse))
                {
                    _error = "服务器返回了 HTTP " + responseCode + "，不是分段内容。";
                    Retryable = responseCode >= 500;
                    return false;
                }

                if (_written + dataLength > _maxBytes)
                {
                    _error = "服务器返回的数据超过了请求的字节区间。";
                    Retryable = false;
                    return false;
                }

                try
                {
                    _stream.Write(data, 0, dataLength);
                    _written += dataLength;
                }
                catch (Exception exception)
                {
                    _error = "写入下载文件失败：" + exception.Message;
                    Retryable = false;
                    return false;
                }

                if (OnData != null)
                    OnData();

                return true;
            }
        }
    }
}
