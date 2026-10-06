using FluentAssertions;
using Inbox.Application.Media;
using Inbox.Application.Options;
using Inbox.Infrastructure.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbox.Infrastructure.Tests;

/// <summary>
/// Storage is where an untrusted file becomes something the system serves back to agents, so these
/// tests are about what it refuses as much as what it writes.
/// </summary>
public sealed class MediaStorageTests : IDisposable
{
    private readonly string _root;
    private readonly string _contentRoot;

    public MediaStorageTests()
    {
        // Both roots are real directories: the validation that rejects a root inside the content root
        // compares resolved paths, so it needs somewhere to resolve against.
        _root = Path.Combine(Path.GetTempPath(), "inbox-media-tests", Guid.NewGuid().ToString("N"));
        _contentRoot = Path.Combine(Path.GetTempPath(), "inbox-media-content", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_contentRoot);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _contentRoot })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private MediaStorage Create(MediaOptions? options = null) =>
        new(
            Options.Create(options ?? WithRoot(_root)),
            new StubEnvironment(_contentRoot),
            NullLogger<MediaStorage>.Instance);

    [Fact]
    public async Task A_valid_image_is_saved_and_read_back_byte_for_byte()
    {
        var storage = Create(WithRoot(_root));
        var bytes = Jpeg();

        var validation = storage.Validate(bytes, "image/jpeg", bytes.Length);
        validation.Accepted.Should().BeTrue();

        var relative = await storage.SaveAsync(bytes, FileSignature.ExtensionFor("image/jpeg"));

        // The stored path is relative, so the absolute location never reaches a row or a response.
        Path.IsPathRooted(relative).Should().BeFalse();

        var read = await storage.ReadAsync(relative);
        read.Should().NotBeNull();
        read!.Content.Should().Equal(bytes);
        read.ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task The_stored_name_is_generated_not_derived_from_the_caller()
    {
        var storage = Create(WithRoot(_root));
        var bytes = Jpeg();

        var first = await storage.SaveAsync(bytes, ".jpg");
        var second = await storage.SaveAsync(bytes, ".jpg");

        // Two copies of the same upload must not collide on one file.
        first.Should().NotBe(second);
        Path.GetFileName(first).Should().MatchRegex("^[0-9a-f]{32}\\.jpg$");
    }

    [Fact]
    public async Task Reading_a_file_that_is_gone_reports_missing_rather_than_throwing()
    {
        var storage = Create(WithRoot(_root));

        var read = await storage.ReadAsync("2026/10/does-not-exist.jpg");

        read.Should().BeNull();
    }

    [Fact]
    public async Task A_stored_path_that_escapes_the_root_is_refused()
    {
        var storage = Create(WithRoot(_root));

        // A row that was tampered with, or written by an older bug. It must never be served, and must
        // not throw into the request that hit it.
        var read = await storage.ReadAsync("../../../../windows/win.ini");

        read.Should().BeNull();
    }

    [Fact]
    public void A_root_inside_the_content_root_is_refused()
    {
        // The check that makes "outside the web root" a guarantee rather than a convention.
        var storage = Create(WithRoot(Path.Combine(_contentRoot, "media")));

        var act = async () => await storage.SaveAsync(Jpeg(), ".jpg");

        act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*outside the web root*");
    }

    [Fact]
    public void An_unset_root_is_refused_rather_than_defaulting_somewhere()
    {
        var storage = Create(WithRoot(string.Empty));

        var act = async () => await storage.SaveAsync(Jpeg(), ".jpg");

        act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Media:RootPath*");
    }

    [Fact]
    public void A_file_larger_than_its_kind_allows_is_refused()
    {
        // 5 MB is Meta's image ceiling. Declared as a jpeg and it is one; the size is what fails.
        var options = WithRoot(_root);
        var oversized = new byte[options.Images.MaxSizeBytes + 1];
        Jpeg().CopyTo(oversized, 0);

        var validation = Create(options).Validate(oversized, "image/jpeg", oversized.Length);

        validation.Accepted.Should().BeFalse();
        validation.Reason.Should().Be(MediaRejectionReason.TooLarge);
        validation.Message.Should().Contain("5 MB");
    }

    [Fact]
    public void A_file_exactly_on_the_ceiling_is_accepted()
    {
        // Boundary: "larger than" not "at least", so a 5 MB image is still allowed.
        var options = WithRoot(_root);
        var exact = new byte[options.Images.MaxSizeBytes];
        Jpeg().CopyTo(exact, 0);

        Create(options).Validate(exact, "image/jpeg", exact.Length).Accepted.Should().BeTrue();
    }

    [Fact]
    public void A_type_outside_the_whitelist_is_refused_even_when_the_bytes_are_a_real_image()
    {
        var options = WithImageWhitelist(_root, "image/png");

        var validation = Create(options).Validate(Jpeg(), "image/jpeg", Jpeg().Length);

        validation.Reason.Should().Be(MediaRejectionReason.ContentTypeNotAllowed);
    }

    [Fact]
    public void A_webp_is_refused_by_the_default_image_whitelist()
    {
        byte[] webp = [0x52, 0x49, 0x46, 0x46, 0x1A, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];

        // WhatsApp stickers arrive as image/webp. They are deliberately not in the whitelist, so a
        // sticker is recorded as unavailable rather than stored.
        var validation = Create(WithRoot(_root)).Validate(webp, "image/webp", webp.Length);

        validation.Reason.Should().Be(MediaRejectionReason.ContentTypeNotAllowed);
    }

    [Fact]
    public void A_mp4_video_is_refused_rather_than_stored_as_audio()
    {
        var bytes = new byte[16];
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        "isom"u8.CopyTo(bytes.AsSpan(8));

        var validation = Create(WithRoot(_root)).Validate(bytes, "audio/ogg; codecs=opus", bytes.Length);

        validation.Reason.Should().Be(MediaRejectionReason.UnrecognisedType);
    }

    [Fact]
    public void A_voice_note_declared_with_codec_parameters_is_accepted()
    {
        // Exactly how Meta labels an inbound voice note.
        var bytes = "OggS\0\x02\0\0\0\0\0\0\x00\x02"u8.ToArray().AsSpan(0, 12).ToArray();

        var validation = Create(WithRoot(_root)).Validate(bytes, "audio/ogg; codecs=opus", bytes.Length);

        validation.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task A_file_declared_as_a_pdf_that_is_really_a_jpeg_is_stored_as_a_jpeg()
    {
        // The declared type says PDF and is ignored, as it must be. The bytes are a JPEG, so that is
        // what gets stored and what would be served - which is the property that matters: it can never
        // come back as application/pdf to a browser that would then run it as a document.
        var storage = Create(WithRoot(_root));
        var bytes = Jpeg();

        var validation = storage.Validate(bytes, "application/pdf", bytes.Length);
        validation.Accepted.Should().BeTrue();

        var relative = await storage.SaveAsync(bytes, FileSignature.ExtensionFor("image/jpeg"));
        var read = await storage.ReadAsync(relative);

        read!.ContentType.Should().Be("image/jpeg");
        read.ContentType.Should().NotBe("application/pdf");
    }

    [Fact]
    public void A_declared_type_outside_the_whitelist_does_not_rescue_a_real_file()
    {
        // The mirror image: declaring a supported type does not get a file past the whitelist. These
        // bytes are nothing we recognise, so no declaration rescues them.
        var validation = Create(WithRoot(_root)).Validate(
            Header(0xDE, 0xAD, 0xBE, 0xEF),
            "image/jpeg",
            64);

        validation.Reason.Should().Be(MediaRejectionReason.UnrecognisedType);
    }

    [Fact]
    public void A_docx_is_accepted_when_the_declared_type_names_an_office_format()
    {
        var bytes = Header(0x50, 0x4B, 0x03, 0x04);

        var validation = Create(WithRoot(_root)).Validate(
            bytes,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            bytes.Length);

        validation.Accepted.Should().BeTrue();
    }

    [Fact]
    public void A_bare_zip_is_refused_because_it_is_not_a_document_we_send()
    {
        var bytes = Header(0x50, 0x4B, 0x03, 0x04);

        var validation = Create(WithRoot(_root)).Validate(bytes, "application/zip", bytes.Length);

        validation.Reason.Should().Be(MediaRejectionReason.UnrecognisedType);
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        var validation = Create(WithRoot(_root)).Validate(ReadOnlyMemory<byte>.Empty, "image/jpeg", 0);

        validation.Reason.Should().Be(MediaRejectionReason.Empty);
    }

    [Fact]
    public void The_overall_ceiling_tightens_a_permissive_kind()
    {
        // A deployment that wants a hard 10 MB limit for everything, regardless of the provider's own
        // ceilings, must be able to impose it.
        var options = WithRoot(_root, maxFileSizeBytes: 1024);

        var bytes = new byte[2048];
        Jpeg().CopyTo(bytes, 0);

        Create(options).Validate(bytes, "image/jpeg", bytes.Length).Reason
            .Should().Be(MediaRejectionReason.TooLarge);
    }

    [Fact]
    public async Task The_type_served_is_sniffed_again_rather_than_read_from_the_row()
    {
        var storage = Create(WithRoot(_root));
        var bytes = Jpeg();

        var relative = await storage.SaveAsync(bytes, FileSignature.ExtensionFor("image/jpeg"));

        // Overwrite the file with different bytes behind storage's back, standing in for a row whose
        // ContentType column was edited. The served type must follow the bytes.
        await File.WriteAllBytesAsync(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)), Jpeg());

        var read = await storage.ReadAsync(relative);
        read!.ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public void Base_content_type_strips_parameters_and_case()
    {
        FileSignature.BaseContentType("audio/ogg; codecs=opus").Should().Be("audio/ogg");
        FileSignature.BaseContentType("TEXT/PLAIN; charset=utf-8").Should().Be("text/plain");
        FileSignature.BaseContentType("image/jpeg").Should().Be("image/jpeg");
    }

    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static byte[] Header(params byte[] prefix)
    {
        var bytes = new byte[64];
        prefix.CopyTo(bytes, 0);

        return bytes;
    }

    private static MediaOptions WithRoot(string root) => WithRoot(root, maxFileSizeBytes: 100 * 1024 * 1024);

    private static MediaOptions WithRoot(string root, long maxFileSizeBytes) => new()
    {
        RootPath = root,
        MaxFileSizeBytes = maxFileSizeBytes,
        DownloadInboundMedia = true,
        Images = new MediaKindLimits { MaxSizeBytes = 5 * 1024 * 1024, AllowedContentTypes = ["image/jpeg", "image/png"] },
        Documents = new MediaKindLimits
        {
            MaxSizeBytes = 100 * 1024 * 1024,
            AllowedContentTypes =
            [
                "application/pdf",
                "text/plain",
                "application/msword",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                "application/vnd.ms-excel",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "application/vnd.ms-powerpoint",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            ]
        },
        Audio = new MediaKindLimits
        {
            MaxSizeBytes = 16 * 1024 * 1024,
            AllowedContentTypes = ["audio/aac", "audio/amr", "audio/mpeg", "audio/mp4", "audio/ogg"]
        }
    };

    /// <summary>Same defaults, but with the image whitelist narrowed to PNG only.</summary>
    private static MediaOptions WithImageWhitelist(string root, params string[] allowed) => new()
    {
        RootPath = root,
        MaxFileSizeBytes = 100 * 1024 * 1024,
        DownloadInboundMedia = true,
        Images = new MediaKindLimits { MaxSizeBytes = 5 * 1024 * 1024, AllowedContentTypes = allowed },
        Documents = WithRoot(root).Documents,
        Audio = WithRoot(root).Audio
    };

    private sealed class StubEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "Inbox.Api";

        public string ContentRootPath { get; set; } = contentRoot;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}