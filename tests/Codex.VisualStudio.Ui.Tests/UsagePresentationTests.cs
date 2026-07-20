using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class UsagePresentationTests
{
    private static readonly string[] UsageCommands =
        ["{Binding OpenUsageDashboardCommand}", "{Binding OpenUsageHelpCommand}", "{Binding CloseUsageCommand}"];

    [TestMethod]
    [DataRow(-10, 100)]
    [DataRow(0, 100)]
    [DataRow(20, 80)]
    [DataRow(100, 0)]
    [DataRow(150, 0)]
    public void Update_ComputesClampedRemainingPercent(int usedPercent, int expectedRemaining)
    {
        var presentation = new UsagePresentation();
        presentation.Update(Result(new RateLimitInfo
        {
            Primary = new RateLimitWindowInfo
            {
                UsedPercent = usedPercent,
                WindowDurationMinutes = 300,
                ResetsAt = 1_800_000_000,
            },
            Secondary = new RateLimitWindowInfo { UsedPercent = 50, WindowDurationMinutes = 10_080 },
        }), DateTimeOffset.UnixEpoch, new SafeMarkdownService());

        StringAssert.StartsWith(presentation.PrimaryText, $"5-hour limit: {expectedRemaining}% remaining");
        StringAssert.StartsWith(presentation.SecondaryText, "Weekly limit: 50% remaining");
        StringAssert.Contains(presentation.PrimaryText, "UTC");
    }

    [TestMethod]
    public void Update_MissingUsedPercent_IsNotPresentedAsUsage()
    {
        var presentation = new UsagePresentation();
        presentation.Update(Result(new RateLimitInfo
        {
            Primary = new RateLimitWindowInfo { WindowDurationMinutes = 300 },
        }), DateTimeOffset.UnixEpoch, new SafeMarkdownService());

        Assert.IsFalse(presentation.HasData);
        Assert.AreEqual("Usage data is unavailable.", presentation.StatusText);
    }

    [TestMethod]
    public void Update_SelectsCanonicalOrExactlyOneMapEntryOnly()
    {
        var presentation = new UsagePresentation();
        presentation.Update(new RateLimitsResult(), DateTimeOffset.UnixEpoch, new SafeMarkdownService());
        Assert.IsFalse(presentation.HasData);

        presentation.Update(new RateLimitsResult
        {
            RateLimitsByLimitId = new Dictionary<string, RateLimitInfo>
            {
                ["one"] = new() { Primary = new() { UsedPercent = 40 } },
            },
        }, DateTimeOffset.UnixEpoch, new SafeMarkdownService());
        Assert.AreEqual("60% remaining", presentation.ToolbarText);

        presentation.Update(new RateLimitsResult
        {
            RateLimitsByLimitId = new Dictionary<string, RateLimitInfo>
            {
                ["other"] = new() { Primary = new() { UsedPercent = 10 } },
                ["CODEX"] = new() { Primary = new() { UsedPercent = 25 } },
            },
        }, DateTimeOffset.UnixEpoch, new SafeMarkdownService());
        Assert.AreEqual("75% remaining", presentation.ToolbarText);

        presentation.Update(new RateLimitsResult
        {
            RateLimitsByLimitId = new Dictionary<string, RateLimitInfo>
            {
                ["one"] = new() { Primary = new() { UsedPercent = 40 } },
                ["two"] = new() { Primary = new() { UsedPercent = 60 } },
            },
        }, DateTimeOffset.UnixEpoch, new SafeMarkdownService());
        Assert.IsFalse(presentation.HasData);
    }

    [TestMethod]
    public void Update_SanitizesAndBoundsCreditBalance()
    {
        var presentation = new UsagePresentation();
        presentation.Update(Result(new RateLimitInfo
        {
            Primary = new() { UsedPercent = 10 },
            Credits = new() { HasCredits = true, Balance = new string('x', 200) + "\r\nnext" },
        }), DateTimeOffset.UnixEpoch, new SafeMarkdownService());

        Assert.IsTrue(presentation.HasCredits);
        Assert.AreEqual(137, presentation.CreditsText.Length);
        Assert.IsFalse(presentation.CreditsText.Contains('\r'));
        Assert.IsFalse(presentation.CreditsText.Contains('\n'));
    }

    [TestMethod]
    public void Update_CreditOnlyResultRemainsAccessibleAndAvailableToStatus()
    {
        var presentation = new UsagePresentation();
        presentation.Update(Result(new RateLimitInfo
        {
            Credits = new() { HasCredits = true, Balance = "<b>12.50</b>\r\nignored" },
        }), DateTimeOffset.UnixEpoch, new SafeMarkdownService());

        Assert.IsTrue(presentation.HasData);
        Assert.IsTrue(presentation.HasCredits);
        Assert.AreEqual("Credit information available.", presentation.StatusText);
        Assert.AreEqual("Credits: 12.50 ignored", presentation.SlashStatusText);
        StringAssert.Contains(presentation.AutomationHelpText, "Credits: 12.50 ignored");
    }

    [TestMethod]
    public async Task ExternalLinkOpener_UsesOnlyApprovedFixedDestinations()
    {
        var starts = new List<ProcessStartInfo>();
        var opener = new ExternalLinkOpener(starts.Add);

        await opener.OpenAsync(ExternalLinkTarget.UsageDashboard, CancellationToken.None);
        await opener.OpenAsync(ExternalLinkTarget.UsageHelp, CancellationToken.None);

        Assert.AreEqual(2, starts.Count);
        Assert.IsTrue(starts.All(value => value.UseShellExecute));
        Assert.IsTrue(starts.All(value => ExternalLinkOpener.IsAllowed(new Uri(value.FileName))));
        Assert.IsFalse(ExternalLinkOpener.IsAllowed(new Uri("http://chatgpt.com/codex/settings/usage")));
        Assert.IsFalse(ExternalLinkOpener.IsAllowed(new Uri("https://chatgpt.com/codex/settings/usage?next=bad")));
        Assert.IsFalse(ExternalLinkOpener.IsAllowed(new Uri("https://chatgpt.com/codex/settings/usage/extra")));
    }

    [TestMethod]
    public void ChatToolWindowXaml_UsagePopup_HasKeyboardAutomationAndThemeContracts()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream);
        XDocument document = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement popup = document.Descendants(presentation + "Popup")
            .Single(value => value.Attribute("IsOpen")?.Value == "{Binding IsUsageOpen, Mode=TwoWay}");
        XElement host = popup.Parent ?? throw new AssertFailedException("Usage popup must have a host element.");
        XElement border = popup.Descendants(presentation + "Border").First();
        Assert.AreEqual("True", popup.Attribute("Focusable")?.Value);
        Assert.AreEqual("True", border.Attribute("FocusManager.IsFocusScope")?.Value);
        Assert.AreEqual("{Binding ElementName=UsageCloseButton}", border.Attribute("FocusManager.FocusedElement")?.Value);
        Assert.AreEqual("Cycle", border.Attribute("KeyboardNavigation.TabNavigation")?.Value);
        Assert.IsNotNull(border.Attribute("AutomationProperties.Name"));
        Assert.IsTrue(host.Elements(presentation + "Grid.InputBindings")
            .Descendants(presentation + "KeyBinding")
            .Any(value => value.Attribute("Key")?.Value == "Escape"
                && value.Attribute("Command")?.Value == "{Binding CloseUsageCommand}"));
        Assert.IsTrue(border.Descendants(presentation + "KeyBinding")
            .Any(value => value.Attribute("Key")?.Value == "Escape"
                && value.Attribute("Command")?.Value == "{Binding CloseUsageCommand}"));
        StringAssert.Contains(border.Attribute("Background")?.Value, "EnvironmentColors.ToolWindowBackgroundBrushKey");
        CollectionAssert.AreEquivalent(
            UsageCommands,
            border.Descendants(presentation + "Button")
                .Select(value => value.Attribute("Command")?.Value)
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray());
    }

    private static RateLimitsResult Result(RateLimitInfo rateLimit)
        => new() { RateLimits = rateLimit };
}
