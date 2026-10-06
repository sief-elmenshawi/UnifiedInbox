using System.Text;
using FluentAssertions;
using Inbox.Application.Media;
using Xunit;

namespace Inbox.Infrastructure.Tests;

/// <summary>
/// The whole point of these tests is that the answer comes from the bytes. Each case declares a
/// <em>different</em> content type from the one it carries, which is the situation the check exists for:
/// a browser sends application/octet-stream for anything unusual, and a hostile caller sends whatever
/// suits it.
/// </summary>
public sealed class FileSignatureTests
{
    [Theory]
    // The declared type is never consulted; showing it in the failure message documents how wrong the
    // claim can be.
    [InlineData("image/jpeg")]
    [InlineData("application/octet-stream")]
    [InlineData("text/html")]
    public void JPEG_bytes_are_an_image_however_they_were_declared(string declared) =>
        FileSignature.Detect(Jpeg().AsSpan()).Should().Be("image/jpeg", "it was declared as {0}", declared);

    [Theory]
    [InlineData("image/png")]
    [InlineData("application/octet-stream")]
    public void PNG_bytes_are_detected_from_the_eight_byte_signature(string declared) =>
        FileSignature.Detect(Png().AsSpan()).Should().Be("image/png", "it was declared as {0}", declared);

    [Fact]
    public void A_PDF_is_detected() =>
        FileSignature.Detect("%PDF-1.7\n1 0 obj\n"u8.ToArray().AsSpan()).Should().Be("application/pdf");

    [Fact]
    public void An_Ogg_voice_note_is_detected_as_audio() =>
        FileSignature.Detect("OggS\0\x02\0\0\0\0\0\0\x00\x02"u8.ToArray().AsSpan()).Should().Be("audio/ogg");

    [Fact]
    public void An_ID3_MP3_is_detected() =>
        FileSignature.Detect("ID3\u0004\0\0\0\0\0\0"u8.ToArray().AsSpan()).Should().Be("audio/mpeg");

    [Fact]
    public void An_ADTS_AAC_frame_is_detected()
    {
        // Sync word 0xFFF, layer 00, protection-absent 1.
        byte[] frame = [0xFF, 0xF1, 0x50, 0x80, 0x00, 0x1F, 0xFC];

        FileSignature.Detect(frame.AsSpan()).Should().Be("audio/aac");
    }

    [Fact]
    public void An_MPEG_audio_frame_is_not_mistaken_for_AAC()
    {
        // 11-bit sync 0xFFE with layer III: same first byte as ADTS, different second. Without the
        // layer check this would be accepted as AAC and then decoded as noise.
        byte[] frame = [0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00, 0x00];

        FileSignature.Detect(frame.AsSpan()).Should().NotBe("audio/aac");
    }

    [Fact]
    public void An_m4a_is_detected_from_its_ftyp_brand() =>
        FileSignature.Detect(Mp4("M4A ").AsSpan()).Should().Be("audio/mp4");

    [Fact]
    public void An_mp4_video_is_not_reported_as_audio()
    {
        // The security-relevant case: mp4 video and m4a share the ftyp box, so accepting any ftyp brand
        // as audio would let a video through the audio whitelist and be served with an audio type.
        FileSignature.Detect(Mp4("isom").AsSpan()).Should().BeNull();
    }

    [Fact]
    public void An_ambiguous_mp4_brand_is_left_unrecognised_rather_than_guessed() =>
        FileSignature.Detect(Mp4("mp42").AsSpan()).Should().BeNull();

    [Fact]
    public void A_webp_is_detected_from_the_riff_fourcc()
    {
        byte[] webp = [0x52, 0x49, 0x46, 0x46, 0x1A, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];

        FileSignature.Detect(webp.AsSpan()).Should().Be("image/webp");
    }

    [Fact]
    public void A_docx_is_a_zip_container()
    {
        // xlsx, docx and pptx are all ZIPs, so the signature can only prove "a zip"; the storage layer is
        // what narrows it to an Office type using the declared type.
        FileSignature.Detect(Header(0x50, 0x4B, 0x03, 0x04).AsSpan()).Should().Be("application/zip");
    }

    [Fact]
    public void A_legacy_doc_is_the_ole2_compound_file()
    {
        byte[] ole2 = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00];

        FileSignature.Detect(ole2.AsSpan()).Should().Be("application/x-ole-storage");
    }

    [Fact]
    public void Plain_text_is_detected() =>
        FileSignature.Detect("Invoice 4821, total 320 EUR\n"u8.ToArray().AsSpan()).Should().Be("text/plain");

    [Fact]
    public void A_binary_blob_is_not_reported_as_text()
    {
        // Printable ASCII with an embedded NUL: the shape of a text file used to smuggle markup past a
        // text-only check. Must not come back as text/plain.
        byte[] polyglot = [0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x00, 0x3E, 0x00, 0x01];

        FileSignature.Detect(polyglot.AsSpan()).Should().BeNull();
    }

    [Fact]
    public void An_empty_buffer_is_not_reported_as_text() =>
        FileSignature.Detect(ReadOnlySpan<byte>.Empty).Should().BeNull();

    [Fact]
    public void Detection_rewinds_the_stream_so_the_caller_can_still_read_it()
    {
        using var stream = new MemoryStream(Jpeg());

        FileSignature.Detect(stream).Should().Be("image/jpeg");
        stream.Position.Should().Be(0);
        stream.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Detection_refuses_a_stream_it_cannot_rewind()
    {
        // A non-seekable stream would be silently drained, leaving the caller with no bytes to store.
        using var stream = new ForwardOnlyStream(Jpeg());

        var act = () => FileSignature.Detect(stream);

        act.Should().Throw<ArgumentException>();
    }

    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
    ];

    private static byte[] Mp4(string brand)
    {
        var bytes = new byte[16];
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        Encoding.ASCII.GetBytes(brand.PadRight(4, ' ')).CopyTo(bytes.AsSpan(8, 4));

        return bytes;
    }

    private static byte[] Header(params byte[] prefix)
    {
        var bytes = new byte[FileSignature.MaxHeaderLength];
        prefix.CopyTo(bytes, 0);

        return bytes;
    }

    /// <summary>A stream that can be read once and never rewound, to prove the guard is real.</summary>
    private sealed class ForwardOnlyStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}