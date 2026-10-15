using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VerticalAppSwitcher
{
    internal sealed class UpdateClient : IDisposable
    {
        private readonly HttpClient client;

        public UpdateClient() : this(new HttpClientHandler()) { }

        internal UpdateClient(HttpMessageHandler handler)
        {
            client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VerticalAppSwitcher/" + UpdateRelease.CurrentVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public async Task<UpdateRelease> CheckAsync(CancellationToken cancellation)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            cancellation = timeout.Token;
            using HttpResponseMessage response = await client.GetAsync(
                UpdateRelease.ApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            using var content = new MemoryStream();
            await CopyLimitedAsync(response, content, 1024 * 1024, cancellation).ConfigureAwait(false);
            return UpdateRelease.Parse(System.Text.Encoding.UTF8.GetString(content.ToArray()),
                UpdateRelease.CurrentVersion, UpdateRelease.CurrentRuntime);
        }

        public async Task<string> DownloadAsync(UpdateRelease release, CancellationToken cancellation)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            cancellation = timeout.Token;
            string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VerticalAppSwitcher", "Updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            try
            {
                string archive = Path.Combine(workspace, release.PackageName);
                using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                using (HttpResponseMessage response = await client.GetAsync(release.PackageUrl,
                    HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await CopyLimitedAsync(response, output, UpdatePackage.MaximumDownloadSize, cancellation).ConfigureAwait(false);
                }

                using var checksumContent = new MemoryStream();
                using (HttpResponseMessage response = await client.GetAsync(release.ChecksumUrl,
                    HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await CopyLimitedAsync(response, checksumContent, 4096, cancellation).ConfigureAwait(false);
                }

                string checksum = System.Text.Encoding.UTF8.GetString(checksumContent.ToArray());
                UpdatePackage.VerifyChecksum(archive, checksum, release.PackageName);
                File.WriteAllText(Path.Combine(workspace, "checksum.txt"), checksum);
                cancellation.ThrowIfCancellationRequested();
                await Task.Run(() => UpdatePackage.Extract(archive, Path.Combine(workspace, "package"),
                    release.Version.ToString(), release.Runtime), cancellation).ConfigureAwait(false);
                return workspace;
            }
            catch
            {
                try { Directory.Delete(workspace, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        private static async Task CopyLimitedAsync(HttpResponseMessage response, Stream output,
            long limit, CancellationToken cancellation)
        {
            if (response.RequestMessage.RequestUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Update downloads require HTTPS.");
            }

            if (response.Content.Headers.ContentLength > limit)
            {
                throw new InvalidDataException("The update response is too large.");
            }

            using Stream input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            byte[] buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > limit)
                {
                    throw new InvalidDataException("The update response is too large.");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
