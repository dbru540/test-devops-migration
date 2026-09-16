using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MigrationTools.Tools
{
    /// <summary>Comment attachment transfer, independently testable without real organizations.</summary>
    public static class CommentImageTransfer
    {
        private const int MaxImageBytes = 20 * 1024 * 1024;

        private static string Organization(Uri uri)
        {
            if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return null;
            if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
                return uri.AbsolutePath.Split('/').FirstOrDefault(p => !string.IsNullOrEmpty(p));
            const string suffix = ".visualstudio.com";
            return uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? uri.Host.Substring(0, uri.Host.Length - suffix.Length) : null;
        }

        public static bool IsAttachmentInOrganization(string value, string collection)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) || !Uri.TryCreate(collection, UriKind.Absolute, out Uri configured)) return false;
            string org = Organization(uri);
            return org != null && org.Equals(Organization(configured), StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.IndexOf("/_apis/wit/attachments/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string Rewrite(string html, string sourceCollection, string targetCollection, string project,
                                     HttpClient download, HttpClient upload, IDictionary<string, string> cache)
        {
            if (string.IsNullOrEmpty(html)) return html;
            if (html.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Comment exceeds supported transfer size");
            string result = html;
            var urls = Regex.Matches(html, @"https?://(?:dev\.azure\.com/[^/]+|[a-z0-9-]+\.visualstudio\.com)/[^""'\s<>]+", RegexOptions.IgnoreCase);
            foreach (Match match in urls)
            {
                string original = WebUtility.HtmlDecode(match.Value);
                if (original.IndexOf("/_apis/wit/attachments/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (IsAttachmentInOrganization(original, targetCollection)) continue;
                if (!IsAttachmentInOrganization(original, sourceCollection)) throw new InvalidOperationException("Comment attachment outside configured organizations");
                string key = targetCollection + "|" + original;
                if (!cache.TryGetValue(key, out string replacement))
                {
                    using (var response = download.GetAsync(original, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                    {
                        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Comment attachment download HTTP " + (int)response.StatusCode);
                        string media = response.Content.Headers.ContentType?.MediaType;
                        if (media == "text/html" || media == "application/json") throw new InvalidOperationException("Comment attachment response is not a file");
                        if (response.Content.Headers.ContentLength > MaxImageBytes) throw new InvalidOperationException("Comment attachment too large");
                        using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        using (var output = new MemoryStream())
                        {
                            var buffer = new byte[81920];
                            int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (output.Length + count > MaxImageBytes) throw new InvalidOperationException("Comment attachment too large");
                                output.Write(buffer, 0, count);
                            }
                            if (output.Length == 0) throw new InvalidOperationException("Empty comment attachment");
                            replacement = Upload(output.ToArray(), targetCollection, project, upload, "attachment");
                        }
                    }
                    cache[key] = replacement;
                }
                result = result.Replace(match.Value, replacement);
            }
            var inlineImages = Regex.Matches(result, @"data:image/(png|jpeg|gif|webp);base64,([A-Za-z0-9+/=\r\n]+)", RegexOptions.IgnoreCase);
            foreach (Match match in inlineImages)
            {
                byte[] bytes = Convert.FromBase64String(match.Groups[2].Value);
                if (bytes.Length == 0 || bytes.Length > MaxImageBytes || !HasImageSignature(bytes, match.Groups[1].Value.ToLowerInvariant()))
                    throw new InvalidOperationException("Invalid inline comment image");
                string key = targetCollection + "|inline|" + Hash(bytes);
                if (!cache.TryGetValue(key, out string replacement))
                {
                    replacement = Upload(bytes, targetCollection, project, upload, match.Groups[1].Value);
                    cache[key] = replacement;
                }
                result = result.Replace(match.Value, replacement);
            }
            if (result.IndexOf("data:image/", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("Unsupported inline comment image encoding");
            return result;
        }

        private static bool HasImageSignature(byte[] bytes, string type)
        {
            if (type == "png") return bytes.Length >= 8 && bytes.Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10});
            if (type == "jpeg") return bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
            if (type == "gif") return bytes.Length >= 6 && System.Text.Encoding.ASCII.GetString(bytes, 0, 3) == "GIF";
            return type == "webp" && bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP";
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string Upload(byte[] bytes, string target, string project, HttpClient client, string extension)
        {
            string url = target.TrimEnd('/') + "/" + Uri.EscapeDataString(project) + "/_apis/wit/attachments?fileName=comment-" + Hash(bytes) + "." + extension + "&api-version=7.1";
            using (var content = new ByteArrayContent(bytes))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using (var response = client.PostAsync(url, content).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Comment attachment upload HTTP " + (int)response.StatusCode);
                    string uploaded = JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())["url"]?.ToString();
                    if (!IsAttachmentInOrganization(uploaded, target)) throw new InvalidOperationException("Uploaded comment attachment outside target organization");
                    return uploaded;
                }
            }
        }
    }
}
