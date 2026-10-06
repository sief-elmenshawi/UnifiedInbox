using FluentAssertions;
using Inbox.Application.Media;
using Xunit;

namespace Inbox.Infrastructure.Tests;

public sealed class FileNameSanitizerTests
{
    [Fact]
    public void An_ordinary_document_name_is_left_alone() =>
        FileNameSanitizer.Sanitize("invoice-4821.pdf").Should().Be("invoice-4821.pdf");

    [Theory]
    [InlineData("passport.jpg")]
    [InlineData("تذكرة الطيران.pdf")]
    [InlineData("boarding pass 2024.pdf")]
    [InlineData("receipt (1).png")]
    [InlineData("Hotel_Booking.xlsx")]
    public void Names_a_person_would_recognise_survive(string name) =>
        FileNameSanitizer.Sanitize(name).Should().Be(name);

    [Theory]
    [InlineData("../../web.config")]
    [InlineData(@"..\..\web.config")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\windows\system32\cmd.exe")]
    public void A_path_in_the_name_is_reduced_to_its_last_segment(string name) =>
        FileNameSanitizer.Sanitize(name).Should().Be(name.Split(['/', '\\']).Last());

    [Fact]
    public void A_name_that_is_only_a_path_yields_the_fallback() =>
        FileNameSanitizer.Sanitize("../../..").Should().Be("attachment");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("///")]
    public void Nothing_usable_becomes_the_fallback(string? name) =>
        FileNameSanitizer.Sanitize(name).Should().Be("attachment");

    [Theory]
    // Header splitting: a name reaching a Content-Disposition header with a CRLF in it can inject
    // response headers, so control characters are removed outright - along with the ':' that follows,
    // which is not a character Windows accepts in a file name either.
    [InlineData("passport\r\nX-Injected: 1.jpg", "passportX-Injected 1.jpg")]
    [InlineData("passport\nX-Injected: 1.jpg", "passportX-Injected 1.jpg")]
    [InlineData("passport\u0000.jpg", "passport.jpg")]
    public void Control_characters_that_could_split_a_header_are_removed(string name, string expected) =>
        FileNameSanitizer.Sanitize(name).Should().Be(expected);

    [Fact]
    public void A_survivor_never_contains_a_line_break()
    {
        // The property that matters, checked directly rather than through one example.
        var sanitized = FileNameSanitizer.Sanitize("a\r\nb\nc\td.jpg");

        sanitized.Should().NotContain("\r").And.NotContain("\n");
    }

    [Theory]
    [InlineData("a<b>c:d\"e|f?g*h.txt")]
    public void Characters_windows_refuses_are_removed(string name) =>
        FileNameSanitizer.Sanitize(name).Should().Be("abcdefgh.txt");

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL.pdf")]
    [InlineData("com1.jpg")]
    public void A_reserved_device_name_becomes_readable(string name) =>
        FileNameSanitizer.Sanitize(name).Should().StartWith("attachment");

    [Fact]
    public void An_overlong_name_is_truncated_but_keeps_its_extension()
    {
        // The extension is what lets an agent and the browser recognise the file, so it is the part
        // that must survive the cut.
        var sanitized = FileNameSanitizer.Sanitize(new string('a', 400) + ".pdf");

        sanitized.Should().HaveLength(FileNameSanitizer.MaxLength);
        sanitized.Should().EndWith(".pdf");
    }

    [Fact]
    public void An_overlong_name_with_no_extension_is_still_truncated() =>
        FileNameSanitizer.Sanitize(new string('b', 900))
            .Should().HaveLength(FileNameSanitizer.MaxLength);

    [Fact]
    public void A_leading_dot_is_stripped_so_the_file_is_not_hidden() =>
        FileNameSanitizer.Sanitize(".htaccess").Should().Be("htaccess");

    [Fact]
    public void The_caller_can_name_the_fallback() =>
        FileNameSanitizer.Sanitize(null, "voice-note").Should().Be("voice-note");
}