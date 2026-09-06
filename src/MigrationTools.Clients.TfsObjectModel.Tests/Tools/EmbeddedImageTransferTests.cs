using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MigrationTools.DataContracts;

namespace MigrationTools.Tools.Tests
{
    [TestClass]
    public class EmbeddedImageTransferTests
    {
        private const string Source = "https://dev.azure.com/source-fixture/";
        private const string Target = "https://dev.azure.com/target-fixture/";
        private const string Image = Source + "_apis/wit/attachments/11111111-1111-1111-1111-111111111111?fileName=image.png";
        private const string Uploaded = Target + "_apis/wit/attachments/22222222-2222-2222-2222-222222222222";
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR4nGNgLDEkCTGMahjVMHw1AAAR8qYBGkt1gwAAAABJRU5ErkJggg==");

        private sealed class Transport : HttpMessageHandler
        {
            public HttpStatusCode Status = HttpStatusCode.OK;
            public byte[] Body = Png;
            public readonly List<string> Requests = new List<string>();
            public readonly List<string> Credentials = new List<string>();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                Requests.Add(request.RequestUri.AbsoluteUri);
                Credentials.Add(Encoding.ASCII.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter)));
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Body) });
            }
        }

        private sealed class Tool : TfsEmbededImagesTool
        {
            public readonly Transport Network = new Transport();
            public readonly List<string> Paths = new List<string>();
            public string UploadResult = Uploaded;
            public int Uploads;
            public Tool() : base(Microsoft.Extensions.Options.Options.Create(new TfsEmbededImagesToolOptions { Enabled = true }), null,
                NullLogger<TfsEmbededImagesTool>.Instance, null) { }
            protected override HttpClient CreateImageDownloadClient() => new HttpClient(Network, false);
            protected override string UploadDownloadedImage(WorkItemData target, string path)
            {
                CollectionAssert.AreEqual(Png, File.ReadAllBytes(path));
                Paths.Add(path); Uploads++;
                return UploadResult;
            }
            public string Rewrite(string html, string source = Source, string target = Target)
                => RewriteAttachmentLinks(html, source, target, "System.Description", null, "unit-source-token");
        }

        [TestMethod, TestCategory("L0")]
        public void CrossOrganizationTransferRewritesImageAndAnchorWithOneUpload()
        {
            var tool = new Tool();
            string html = $"<img src=\"{Image}\"><a href=\"{Image}\">image</a>";
            string result = tool.Rewrite(html);
            Assert.AreEqual(html.Replace(Image, Uploaded), result);
            Assert.AreEqual(1, tool.Network.Requests.Count);
            Assert.AreEqual(1, tool.Uploads);
            Assert.AreEqual(":unit-source-token", tool.Network.Credentials[0]);
            Assert.AreEqual(result, tool.Rewrite(html));
            Assert.AreEqual(1, tool.Uploads, "A verified cached transfer must be reused");
            Assert.IsFalse(File.Exists(tool.Paths[0]));
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(tool.Paths[0])));
        }

        [TestMethod, TestCategory("L0")]
        public void ReverseTransferUsesItsOwnSourceAndTarget()
        {
            var tool = new Tool { UploadResult = Source + "_apis/wit/attachments/reverse-copy" };
            string url = Target + "_apis/wit/attachments/original?fileName=reverse.png";
            Assert.AreEqual($"<img src=\"{tool.UploadResult}\">", tool.Rewrite($"<img src=\"{url}\">", Target, Source));
            Assert.AreEqual(url, tool.Network.Requests[0]);
        }

        [TestMethod, TestCategory("L0")]
        public void SameOrganizationAndNonAzureCollectionsDoNotTriggerTransfers()
        {
            var tool = new Tool();
            string html = $"<img src=\"{Image}\">";
            Assert.AreEqual(html, tool.Rewrite(html, Source, Source));
            Assert.AreEqual(html, tool.Rewrite(html, "http://tfs.local/collection/", "http://tfs.local/target/"));
            Assert.AreEqual(0, tool.Network.Requests.Count);
        }

        [TestMethod, TestCategory("L0")]
        public void UnconfiguredOrganizationIsRejectedBeforeCredentialsAreSent()
        {
            var tool = new Tool();
            Assert.ThrowsException<InvalidOperationException>(() => tool.Rewrite("<img src=\"https://dev.azure.com/third-fixture/_apis/wit/attachments/x?fileName=x.png\">"));
            Assert.AreEqual(0, tool.Network.Requests.Count);
        }

        [TestMethod, TestCategory("L0")]
        public void WrongUploadDestinationIsNotCachedOrWrittenIntoHtml()
        {
            var tool = new Tool { UploadResult = Source + "_apis/wit/attachments/wrong-destination" };
            Assert.ThrowsException<InvalidOperationException>(() => tool.Rewrite($"<img src=\"{Image}\">"));
            tool.UploadResult = Uploaded;
            Assert.IsTrue(tool.Rewrite($"<img src=\"{Image}\">").Contains(Uploaded));
            Assert.AreEqual(2, tool.Uploads);
        }

        [TestMethod, TestCategory("L0")]
        public void FilenamesCannotEscapeTheirUniqueTemporaryDirectory()
        {
            var tool = new Tool();
            string url = Image.Replace("image.png", "..%2F..%2Fescape.png") + "&amp;download=true";
            tool.Rewrite($"<img src=\"{url}\">");
            Assert.AreEqual("escape.png", Path.GetFileName(tool.Paths[0]));
            StringAssert.StartsWith(Path.GetFileName(Path.GetDirectoryName(tool.Paths[0])), "devopssync-image-");
            Assert.IsFalse(File.Exists(tool.Paths[0]));
        }

        [TestMethod, TestCategory("L0")]
        public void AttachmentWithoutFilenameStillTransfers()
        {
            var tool = new Tool();
            Assert.IsTrue(tool.Rewrite($"<img src=\"{Image.Split('?')[0]}\">").Contains(Uploaded));
            Assert.AreEqual(1, tool.Uploads);
        }

        [TestMethod, TestCategory("L0")]
        public void AuthenticationAndServerFailuresAreNotSilentlyIgnored()
        {
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable })
            {
                var tool = new Tool(); tool.Network.Status = status;
                Assert.ThrowsException<HttpRequestException>(() => tool.Rewrite($"<img src=\"{Image}\">"));
                Assert.AreEqual(0, tool.Uploads);
            }
        }

        [TestMethod, TestCategory("L0")]
        public void NonImageAndEmptyResponsesNeverReachUpload()
        {
            foreach (var content in new[] { new byte[0], Encoding.UTF8.GetBytes("<html>sign in</html>") })
            {
                var tool = new Tool(); tool.Network.Body = content;
                Exception failure = null;
                try { tool.Rewrite($"<img src=\"{Image}\">"); } catch (Exception ex) { failure = ex; }
                Assert.IsNotNull(failure);
                Assert.AreEqual(0, tool.Uploads);
            }
        }

        [TestMethod, TestCategory("L0")]
        public void LegacyMissingImage404PolicyRemainsExplicit()
        {
            var tool = new Tool(); tool.Network.Status = HttpStatusCode.NotFound;
            string html = $"<img src=\"{Image}\">";
            Assert.AreEqual(html, tool.Rewrite(html));
            Assert.AreEqual(0, tool.Uploads);
        }
    }
}
