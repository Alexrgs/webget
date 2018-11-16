using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace webget
{
    internal class Getter
    {
        private readonly IList<string> _visitedUris = new List<string>();

        public ProxySettings ProxyData { get; set; }
        public string UserAgent { get; set; }
        public string RecursionTarget { get; set; }
        public string Uri { get; set; }
        public string SaveDirectory { get; set; }
        public string NameFilter { get; set; }
        public string[] Extensions { get; set; }
        public int GreaterThan { get; set; }
        public int LessThan { get; set; }
        public int RecursionDepth { get; set; }
        public int RequestTimeout { get; set; }
        public bool LinkLabel { get; set; }
        private int CursorLeft { get; set; }
        private int CursorTop { get; set; }
        private static readonly object lockconsole = new object();

        public void Execute()
        {
            if (!Directory.Exists(SaveDirectory))
                Directory.CreateDirectory(SaveDirectory);

            using (var client = new NetClient
            {
                ProxyData = ProxyData,
                Encoding = Encoding.UTF8,
                UserAgent = UserAgent,
                RequestTimeout = RequestTimeout

            })
            {

                ExecuteInternal(client, Uri, Extensions, SaveDirectory, RecursionDepth);
            }
        }

        public AsyncCompletedEventHandler DownloadFileCompleted(int cursorLeft, int cursorTop)
        {
            Action<object, AsyncCompletedEventArgs> action = (sender, e) =>
            {
                WriteSameLineToconsole(cursorLeft, cursorTop, "Completed");
            };
            return new AsyncCompletedEventHandler(action);
        }

        private DownloadProgressChangedEventHandler UpdateDownloadStatus(int cursorLeft, int cursorTop,string filename)
        {
            Action<object, DownloadProgressChangedEventArgs> action = (sender, args) =>
            {

                var text = $"{filename} {Environment.NewLine} Progress.. {args.ProgressPercentage} % Received: {args.BytesReceived / 1024} Kb from  {args.TotalBytesToReceive / 1024} Kb "  ;

                WriteSameLineToconsole(cursorLeft, cursorTop, text);
            };
            return new DownloadProgressChangedEventHandler(action);

        }

        private static void WriteSameLineToconsole(int cursorLeft, int cursorTop, string text)
        {
            lock (lockconsole)
            {
                Console.SetCursorPosition(cursorLeft, cursorTop);

                Console.Write(text);
            }
        }

        private static void WriteNewLineToconsole(string text)
        {
            lock (lockconsole)
            {
                Console.WriteLine(text);
            }
        }

        private void ExecuteInternal(NetClient client, string uri, string[] extensions, string directory,
                                     int maxDepth, int currentDepth = 0)
        {
            _visitedUris.Add(uri);
            var content = WebUtility.HtmlDecode(DownloadString(client, uri, currentDepth));
            if (!string.IsNullOrEmpty(content))
            {
                var resources = ContentParser.ExtractUris(content)
                                             .Where(x => x.Value.EndsWithAny(extensions))
                                             .Select(x => new UriData
                                             {
                                                 Value = x.Value.ToAbsoluteUri(uri),
                                                 Label = x.Label.Normailze(100)
                                             })
                                             .Distinct(x => x.Value);
                DownloadFiles(client, resources, directory, currentDepth).GetAwaiter().GetResult();

                if (maxDepth < 0 || currentDepth < maxDepth)
                {
                    var sites = ContentParser.ExtractUris(content)
                                             .Where(x => x.Value.WithoutExtension())
                                             .Select(x => x.Value.ToAbsoluteUri(uri))
                                             .Where(x => !_visitedUris.Contains(x, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(RecursionTarget))
                        sites = sites.Where(x => (new Regex(RecursionTarget, RegexOptions.IgnoreCase)).IsMatch(x));

                    currentDepth++;
                    foreach (var s in sites)
                    {
                        ExecuteInternal(client, s, extensions, directory, maxDepth, currentDepth);
                    }
                }
            }
        }

        private string DownloadString(NetClient client, string uri, int currentDepth)
        {
            try
            {
                Console.WriteLine(@"[--> {0}]: ""{1}""...", currentDepth, uri);
                return client.DownloadString(uri);
            }
            catch (Exception ex)
            {
                Console.WriteLine("error: {0}", ex.Message);
                return null;
            }
        }

        private async Task DownloadFiles(NetClient client, IEnumerable<UriData> uris, string directory, int currentDepth)
        {
            var i = -1;
            var maxConcurrency = 5;

            var allTasks = new List<Task>();
            var throttler = new SemaphoreSlim(initialCount: 5);

            foreach (var uri in uris)
            {

                // do an async wait until we can schedule again
                await throttler.WaitAsync();

                var name = LinkLabel && !string.IsNullOrEmpty(uri.Label)
                               ? string.Format("{0}.{1}", uri.Label, uri.Value.Split('.').Last())
                               : uri.Value.Split('/').Last();

                if (!string.IsNullOrEmpty(NameFilter) &&
                    !(new Regex(NameFilter, RegexOptions.IgnoreCase)).IsMatch(name))
                    continue;

                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    Console.WriteLine(@"""{0}"" already exists, skipping...", name);
                    continue;
                }



                try
                {
                    if (GreaterThan > 0 || LessThan > 0)
                    {
                        var size = DownloadHeader(client, uri.Value, "Content-Length");
                        int contentLength;
                        if (int.TryParse(size, out contentLength))
                        {
                            if (GreaterThan > 0 && contentLength < GreaterThan)
                                continue;
                            if (LessThan > 0 && contentLength > LessThan)
                                continue;
                        }
                    }
                   
                    // using Task.Run(...) to run the lambda in its own parallel
                    // flow on the threadpool
                    allTasks.Add(
                        Task.Run(async () =>
                        {
                            try
                            {

                                var localClient = new NetClient()
                                {
                                    ProxyData = ProxyData,
                                    Encoding = Encoding.UTF8,
                                    UserAgent = UserAgent,
                                    RequestTimeout = RequestTimeout
                                };

                                var file = string.Format(@"[{0}.{1}]: downloading ""{2}""...", currentDepth, ++i, name);
                                WriteNewLineToconsole("");

                                localClient.DownloadFileCompleted += DownloadFileCompleted(Console.CursorLeft, Console.CursorTop);
                                localClient.DownloadProgressChanged += UpdateDownloadStatus(Console.CursorLeft, Console.CursorTop, file);

                                WriteNewLineToconsole("");
                                await localClient.DownloadFileTaskAsync(uri.Value, path);
                            }
                            finally
                            {
                                throttler.Release();
                            }
                        }));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("error: {0}", ex.Message);
                }

            }

            // won't get here until all urls have been put into tasks
            await Task.WhenAll(allTasks);
        }

        private string DownloadHeader(NetClient client, string uri, string header)
        {
            client.HeadOnly = true;
            client.DownloadData(uri);
            client.HeadOnly = false;
            return client.ResponseHeaders.Get(header);
        }
    }
}