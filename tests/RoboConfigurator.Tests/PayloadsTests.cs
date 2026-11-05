using System;
using System.IO;
using System.Net;
using System.Text;
using RoboConfigurator.Core;
using Xunit;

namespace RoboConfigurator.Tests
{
    public class PayloadsTests : IDisposable
    {
        private readonly TempDir _dir = new TempDir();
        public void Dispose() => _dir.Dispose();

        private string Write(string name, byte[] content) { string p = _dir.File(name); File.WriteAllBytes(p, content); return p; }
        private string Write(string name, string content) => Write(name, Encoding.UTF8.GetBytes(content));

        // Exe signé (Authenticode intégré) présent sur tout PC avec le SDK .NET, y compris la CI
        private static readonly string SignedExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\dotnet.exe");

        [Fact]
        public void Zip_Ok() => Assert.NotNull(Payloads.Verify(new Payload("a.zip", ""), Write("a.zip", new byte[] { 0x50, 0x4B, 3, 4, 0, 0 })));

        [Fact]
        public void Zip_HtmlPageIsRejectedWithAClearMessage()
        {
            Lang.Set("en");
            var ex = Assert.Throws<InvalidPayloadException>(() => Payloads.Verify(new Payload("a.zip", ""), Write("a.zip", "\r\n  <!DOCTYPE html><html>Not found</html>")));
            Assert.Contains("web page", ex.Message);
        }

        [Fact]
        public void EmptyFileIsRejected() => Assert.Throws<InvalidPayloadException>(() => Payloads.Verify(new Payload("a.zip", ""), Write("a.zip", new byte[0])));

        [Fact]
        public void Cmd_Ok() => Payloads.Verify(new Payload("MAS_AIO.cmd", ""), Write("MAS_AIO.cmd", "@::Please don't edit\r\n@echo off"));

        [Fact]
        public void Cmd_BinaryIsRejected() => Assert.Throws<InvalidPayloadException>(() => Payloads.Verify(new Payload("x.cmd", ""), Write("x.cmd", new byte[] { 0x40, 0, 1, 2 })));

        [Fact]
        public void Exe_NotMzIsRejected() => Assert.Throws<InvalidPayloadException>(() => Payloads.Verify(new Payload("x.exe", ""), Write("x.exe", "hello")));

        [Fact]
        public void Exe_UnsignedIsRejectedWhenASignerIsExpected() =>
            Assert.Throws<InvalidPayloadException>(() => Payloads.Verify(new Payload("x.exe", "", Payloads.MicrosoftSigner), Write("x.exe", new byte[] { 0x4D, 0x5A, 0x90, 0 })));

        [Fact]
        public void Exe_WithoutExpectedSigner_OnlyFormatIsChecked() => Payloads.Verify(new Payload("x.exe", ""), Write("x.exe", new byte[] { 0x4D, 0x5A, 0x90, 0 }));

        [Fact]
        public void Exe_SignedByTheExpectedSigner()
        {
            if (!File.Exists(SignedExe)) return;
            Assert.Contains(".NET", Payloads.Verify(new Payload("dotnet.exe", "", ".NET"), SignedExe));
        }

        [Fact]
        public void Exe_SignedBySomeoneElseIsRejected()
        {
            if (!File.Exists(SignedExe)) return;
            Assert.Throws<InvalidPayloadException>(() => Payloads.Verify(new Payload("dotnet.exe", "", Payloads.NiniteSigner), SignedExe));
        }

        [Fact]
        public void Sha256_Checked()
        {
            string path = Write("a.cmd", "@echo off");
            const string wrong = "5A6D3F8D4E3A1F0A8C1B9F6E2D4C7B8A9E0F1D2C3B4A5968778695A4B3C2D1E0";
            Assert.Throws<InvalidPayloadException>(() => Payloads.Verify(new Payload("a.cmd", "", null, wrong), path));
            string real;
            using (var sha = System.Security.Cryptography.SHA256.Create()) real = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
            Assert.Contains("SHA-256 OK", Payloads.Verify(new Payload("a.cmd", "", null, real.ToLowerInvariant()), path));
        }

        [Fact]
        public void Ensure_ReplacesAnInvalidFileAlreadyPresent()
        {
            // Fichier remplacé par une page web : il est supprimé puis retéléchargé (ici, échec réseau voulu)
            Write("CrapFixer.zip", "<html>");
            var payload = new Payload("CrapFixer.zip", "http://127.0.0.1:9/none.zip");
            var delays = Payloads.RetryDelaysSeconds;
            Payloads.RetryDelaysSeconds = new[] { 0, 0 };
            try { Assert.ThrowsAny<Exception>(() => Payloads.Ensure(payload, _dir.Path)); }
            finally { Payloads.RetryDelaysSeconds = delays; }
            Assert.False(File.Exists(_dir.File("CrapFixer.zip")));
            Assert.False(File.Exists(_dir.File("CrapFixer.zip.part")));
        }

        [Fact]
        public void CheckLink_UnreachableServer_ClearError()
        {
            Lang.Set("en");
            var ex = Assert.ThrowsAny<Exception>(() => Payloads.CheckLink(new Payload("CrapFixer.zip", "http://127.0.0.1:9/none.zip")));
            Assert.Contains("CrapFixer.zip", ex.Message);
            Assert.Contains("127.0.0.1", ex.Message);
        }

        [Theory]
        [InlineData(404, "404")]
        [InlineData(403, "403")]
        [InlineData(503, "503")]
        public void Describe_HttpErrors(int status, string expected) =>
            Assert.Contains(expected, Payloads.Describe(new DownloadHttpException(status, "x")));

        [Fact]
        public void Describe_NoInternet()
        {
            Lang.Set("en");
            var ex = new Exception("outer", new WebException("raw", WebExceptionStatus.NameResolutionFailure));
            Assert.Contains("no Internet connection", Payloads.Describe(ex));
        }
    }
}
