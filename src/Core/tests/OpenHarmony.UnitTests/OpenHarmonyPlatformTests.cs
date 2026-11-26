using Microsoft.Maui;
using Microsoft.Maui.Platform;
using Xunit;

namespace Microsoft.Maui.Core.OpenHarmony.UnitTests;

/// <summary>
/// Host-runnable checks for the pure managed contracts in the OpenHarmony platform slice. These
/// intentionally avoid native calls (the host library is absent off-device) and assert the
/// mappings the slice publishes to the platform.
/// </summary>
public class OpenHarmonyPlatformTests
{
    [Theory]
    [InlineData("aBc", TextTransform.None, "aBc")]
    [InlineData("aBc", TextTransform.Uppercase, "ABC")]
    [InlineData("aBc", TextTransform.Lowercase, "abc")]
    [InlineData("", TextTransform.Uppercase, "")]
    public void ApplyTextTransform_MatchesTheMauiTextTransformContract(
        string text, TextTransform transform, string expected)
    {
        Assert.Equal(expected, OpenHarmonyFormattedText.ApplyTextTransform(text, transform));
    }

    [Fact]
    public void ActionsFor_ButtonPublishesClickOnly()
    {
        IReadOnlyList<OpenHarmonyAccessibilityAction> actions = OpenHarmonyAccessibility.ActionsFor("button");

        Assert.Equal(new[] { OpenHarmonyAccessibilityAction.Click }, actions);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("checkBox")]
    [InlineData("switch")]
    public void ActionsFor_OtherClickRolesPublishClickOnly(string role)
    {
        Assert.Equal(
            new[] { OpenHarmonyAccessibilityAction.Click },
            OpenHarmonyAccessibility.ActionsFor(role));
    }

    [Fact]
    public void ActionsFor_TextInputPublishesEditActionsButNotScroll()
    {
        IReadOnlyList<OpenHarmonyAccessibilityAction> actions = OpenHarmonyAccessibility.ActionsFor("textInput");

        Assert.Contains(OpenHarmonyAccessibilityAction.Click, actions);
        Assert.Contains(OpenHarmonyAccessibilityAction.Copy, actions);
        Assert.Contains(OpenHarmonyAccessibilityAction.Paste, actions);
        Assert.Contains(OpenHarmonyAccessibilityAction.Cut, actions);
        Assert.Contains(OpenHarmonyAccessibilityAction.SelectText, actions);
        Assert.DoesNotContain(OpenHarmonyAccessibilityAction.ScrollForward, actions);
        Assert.DoesNotContain(OpenHarmonyAccessibilityAction.ScrollBackward, actions);
    }

    [Theory]
    [InlineData("slider")]
    [InlineData("scroll")]
    public void ActionsFor_ScrollableRolesPublishBothDirections(string role)
    {
        IReadOnlyList<OpenHarmonyAccessibilityAction> actions = OpenHarmonyAccessibility.ActionsFor(role);

        Assert.Equal(
            new[]
            {
                OpenHarmonyAccessibilityAction.ScrollForward,
                OpenHarmonyAccessibilityAction.ScrollBackward,
            },
            actions);
    }

    [Fact]
    public void ActionsFor_UnknownRolePublishesNothing()
    {
        Assert.Empty(OpenHarmonyAccessibility.ActionsFor("not-a-role"));
    }

    [Theory]
    [InlineData("res/icon.png", true)]
    [InlineData("nested/res/icon.png", true)]
    [InlineData("../secret", false)]
    [InlineData("/absolute.png", false)]
    [InlineData("back\\slash.png", false)]
    [InlineData("double//slash.png", false)]
    [InlineData("res/../escape.png", false)]
    [InlineData("", false)]
    public void IsSafeRawFileName_AcceptsOnlyRelativeForwardSlashPaths(string name, bool expected)
    {
        Assert.Equal(expected, OpenHarmonyMediaElementSurface.IsSafeRawFileName(name, out _));
    }

    [Theory]
    [InlineData("/data/storage/el2/base/files/movie.mp4", true)]
    [InlineData("/data/storage/el2/base/files/../escape.mp4", false)]
    [InlineData("relative/movie.mp4", false)]
    [InlineData("/data/storage/el2/base/files\\movie.mp4", false)]
    [InlineData("", false)]
    public void IsSafeFilePath_AcceptsOnlyAbsoluteSandboxPaths(string path, bool expected)
    {
        Assert.Equal(expected, OpenHarmonyMediaElementSurface.IsSafeFilePath(path, out _));
    }
}
