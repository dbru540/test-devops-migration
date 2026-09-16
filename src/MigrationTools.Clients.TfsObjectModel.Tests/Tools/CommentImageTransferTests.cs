using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MigrationTools.Processors;
using MigrationTools.Tools;

namespace MigrationTools.Clients.TfsObjectModel.Tests.Tools
{
    [TestClass]
    public class CommentImageTransferTests
    {
        private sealed class Handler : HttpMessageHandler
        {
            public Func<HttpRequestMessage, HttpResponseMessage> Respond;
            public int Count;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Count++;
                return Task.FromResult(Respond(request));
            }
        }

        private const string Source = "https://dev.azure.com/source-test";
        private const string Target = "https://dev.azure.com/target-test";
        private const string Original = Source + "/P/_apis/wit/attachments/source-id?fileName=image.png";
        private const string Uploaded = Target + "/P/_apis/wit/attachments/target-id";
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aF1kAAAAASUVORK5CYII=");

        [TestMethod, TestCategory("L0")]
        public void LargeCommentImagesAreCopiedAndRewrittenOnce()
        {
            var download = new Handler { Respond = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) } };
            var upload = new Handler { Respond = req => {
                CollectionAssert.AreEqual(Png, req.Content.ReadAsByteArrayAsync().Result);
                StringAssert.StartsWith(req.RequestUri.ToString(), Target + "/P/_apis/wit/attachments?");
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"url\":\"" + Uploaded + "\"}") };
            }};
            using (var a = new HttpClient(download)) using (var b = new HttpClient(upload))
            {
                string html = new string('x', 100000) + "<img src='" + Original + "'><img src='" + Original + "'>";
                string result = CommentImageTransfer.Rewrite(html, Source, Target, "P", a, b, new Dictionary<string,string>());
                Assert.AreEqual(1, download.Count); Assert.AreEqual(1, upload.Count);
                Assert.IsFalse(result.Contains(Original)); Assert.IsTrue(result.Contains(Uploaded));
                Assert.IsTrue(result.StartsWith(new string('x', 100000)));
            }
        }

        [TestMethod, TestCategory("L0")]
        public void InlineBase64ImageIsUploadedAndRemovedFromHtml()
        {
            var download = new Handler { Respond = _ => throw new Exception("No source download expected") };
            var upload = new Handler { Respond = _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"url\":\"" + Uploaded + "\"}") } };
            using (var a = new HttpClient(download)) using (var b = new HttpClient(upload))
            {
                string html = "<img src='data:image/png;base64," + Convert.ToBase64String(Png) + "'>";
                string result = CommentImageTransfer.Rewrite(html, Source, Target, "P", a, b, new Dictionary<string,string>());
                Assert.IsFalse(result.Contains("base64")); Assert.IsTrue(result.Contains(Uploaded));
                Assert.AreEqual(0, download.Count); Assert.AreEqual(1, upload.Count);
            }
        }

        [DataTestMethod, TestCategory("L0")]
        [DataRow(401)] [DataRow(403)] [DataRow(404)] [DataRow(500)] [DataRow(302)]
        public void FailedImageDownloadIsNotSilentlyIgnored(int status)
        {
            var handler = new Handler { Respond = _ => new HttpResponseMessage((HttpStatusCode)status) };
            using (var client = new HttpClient(handler))
                Assert.ThrowsException<InvalidOperationException>(() => CommentImageTransfer.Rewrite("<img src='"+Original+"'>", Source, Target, "P", client, client, new Dictionary<string,string>()));
        }

        [TestMethod, TestCategory("L0")]
        public void ForeignOrganizationAndSpoofedHostAreNeverAuthorized()
        {
            Assert.IsFalse(CommentImageTransfer.IsAttachmentInOrganization("https://dev.azure.com/evil/P/_apis/wit/attachments/id", Source));
            Assert.IsFalse(CommentImageTransfer.IsAttachmentInOrganization("https://dev.azure.com.attacker.invalid/source-test/_apis/wit/attachments/id", Source));
            Assert.IsFalse(CommentImageTransfer.IsAttachmentInOrganization("http://dev.azure.com/source-test/_apis/wit/attachments/id", Source));
            Assert.IsFalse(CommentImageTransfer.IsAttachmentInOrganization("https://user@dev.azure.com/source-test/_apis/wit/attachments/id", Source));
            Assert.IsTrue(CommentImageTransfer.IsAttachmentInOrganization("https://source-test.visualstudio.com/P/_apis/wit/attachments/id", Source));
        }

        [TestMethod, TestCategory("L0")]
        public void ExistingTargetImageDoesNotGetUploadedAgain()
        {
            var handler = new Handler { Respond = _ => throw new Exception("No request expected") };
            using (var client = new HttpClient(handler))
            {
                string html = "<img src='" + Uploaded + "'>";
                Assert.AreEqual(html, CommentImageTransfer.Rewrite(html, Source, Target, "P", client, client, new Dictionary<string,string>()));
            }
        }

        [TestMethod, TestCategory("L0")]
        public void SameOrganizationDifferentProjectsStillCopiesImageForTargetPermissions()
        {
            var handler = new Handler { Respond = req => req.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) }
                : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"url\":\"" + Original + "\"}") } };
            using (var client = new HttpClient(handler))
            {
                CommentImageTransfer.Rewrite("<img src='"+Original+"'>", Source, Source, "OtherProject", client, client, new Dictionary<string,string>());
                Assert.AreEqual(2, handler.Count);
            }
        }

        [TestMethod, TestCategory("L0")]
        public void LiteralDataUriInPlainTextIsNotTreatedAsAnImage()
        {
            var handler = new Handler { Respond = _ => throw new Exception("No request expected") };
            using (var client = new HttpClient(handler))
            {
                string text = "Example: data:image/png;base64,not-a-real-image";
                Assert.AreEqual(text, CommentImageTransfer.Rewrite(text, Source, Target, "P", client, client, new Dictionary<string,string>()));
            }
        }

        [TestMethod, TestCategory("L0")]
        public void UnsafeUploadResponseAndInvalidInlineImageFail()
        {
            var handler = new Handler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"url\":\"https://evil.invalid/image\"}") } };
            using (var client = new HttpClient(handler))
            {
                Assert.ThrowsException<InvalidOperationException>(() => CommentImageTransfer.Rewrite("<img src='data:image/png;base64,"+Convert.ToBase64String(Png)+"'>", Source, Target, "P", client, client, new Dictionary<string,string>()));
                Assert.ThrowsException<InvalidOperationException>(() => CommentImageTransfer.Rewrite("<img src='data:image/png;base64,"+Convert.ToBase64String(Encoding.UTF8.GetBytes("not an image"))+"'>", Source, Target, "P", client, client, new Dictionary<string,string>()));
            }
        }

        [TestMethod, TestCategory("L0")]
        public void LargeDeltaFilePreservesUnicodeAndRejectsMalformedJson()
        {
            var method = typeof(TfsWorkItemMigrationProcessor).GetMethod("ReadEventDeltaFile", BindingFlags.NonPublic|BindingFlags.Static);
            string path = Path.GetTempFileName();
            try
            {
                string json = "{\"System.Description\":{\"newValue\":\"" + new string('\u00e9', 100000) + "\"}}";
                File.WriteAllText(path, json, Encoding.UTF8);
                Assert.AreEqual(json, method.Invoke(null, new object[] { path }));
                File.WriteAllText(path, "invalid");
                Assert.ThrowsException<TargetInvocationException>(() => method.Invoke(null, new object[] { path }));
            }
            finally { File.Delete(path); }
        }
    }
}
