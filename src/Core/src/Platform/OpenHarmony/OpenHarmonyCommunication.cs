// Email / SMS / phone dialer for OpenHarmony, all through the existing startAbility bridge
// (OpenHarmonyAbilityBridge in OpenHarmonyAppLauncher.cs, kind 0 "open URI"): mailto:, sms: and
// tel: URIs are handed to the shell's implicit ohos.want.action.viewData Want, which the shell
// forwards to UIAbilityContext.startAbility. Like the launcher, every method degrades to a
// no-op and returns without throwing when libopenharmonyhost.so or the shell sink is absent.
//
// Email: EmailMessage is encoded exactly like Microsoft.Maui.Essentials' own shared
// EmailImplementation.GetMailToUri (rc.1): "mailto:?to=<recipients>&cc=...&bcc=...&subject=...
// &body=..." with every value escaped (probed against the shipped assembly). A null message is
// treated as an empty EmailMessage because Email.ComposeAsync() reaches the platform with null.
// Attachments are NOT carried: the kind 0 viewData Want can only open one URI (mailto:), and the
// only file-capable kind (kind 3, FileUriForPath) shares a file with a separate sendData Want
// instead of composing a message. A single attachment therefore cannot be attached here; the
// compose still opens and the drop is logged, and apps that need to send a file can use
// Microsoft.Maui.ApplicationModel.DataTransfer.Share.RequestAsync(new ShareFileRequest(...)),
// which rides kind 3.
//
// SMS: "sms:<recipients joined by ','>[?body=<escaped body>]" (RFC 5724; the iOS/Android
// implementations use the same recipient-first shape).
//
// Phone dialer: "tel:<number>" after MAUI's own validation (null/empty/whitespace throws
// ArgumentNullException(nameof(number)), matching PhoneDialerImplementation.ValidateOpen).
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>MAUI Essentials email on OpenHarmony (mailto: over the startAbility bridge).</summary>
public sealed class OpenHarmonyEmail : IEmail
{
    public static readonly OpenHarmonyEmail Instance = new();

    /// <summary>True when the ability bridge and the shell's startAbility sink are reachable.</summary>
    public bool IsComposeSupported => OpenHarmonyAbilityBridge.IsAvailable;

    public Task ComposeAsync(EmailMessage? message)
    {
        if (message?.Attachments is { Count: > 0 })
        {
            // Documented drop: one sendData Want per launch and only one URI per Want, so the
            // mailto compose cannot carry a file and still be an email compose. See the header.
            OpenHarmonyBridge.WriteStatus(
                $"[maui] email compose dropped {message.Attachments.Count} attachment(s): the mailto bridge cannot carry files");
        }
        string uri = BuildMailToUri(message);
        if (!OpenHarmonyAbilityBridge.TryOpenUri(uri))
        {
            // No recipient/subject/body is logged: mailto: URIs carry addresses and message text.
            OpenHarmonyBridge.WriteStatus("[maui] email compose could not be dispatched");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The mailto URI for <paramref name="message"/>, mirroring the shape MAUI's shipped
    /// shared EmailImplementation.GetMailToUri produces (probed in this increment):
    /// every parameter is emitted in to/cc/bcc/subject/body order with Uri.EscapeDataString,
    /// the query separator is present even without parameters ("mailto:?"), and a null message
    /// is treated as an empty one so Email.ComposeAsync() does not throw.
    /// </summary>
    internal static string BuildMailToUri(EmailMessage? message)
    {
        message ??= new EmailMessage();
        var parameters = new List<string>();
        string to = Recipients(message.To);
        if (to.Length > 0)
        {
            parameters.Add("to=" + to);
        }
        string cc = Recipients(message.Cc);
        if (cc.Length > 0)
        {
            parameters.Add("cc=" + cc);
        }
        string bcc = Recipients(message.Bcc);
        if (bcc.Length > 0)
        {
            parameters.Add("bcc=" + bcc);
        }
        if (!string.IsNullOrEmpty(message.Subject))
        {
            parameters.Add("subject=" + Uri.EscapeDataString(message.Subject));
        }
        if (!string.IsNullOrEmpty(message.Body))
        {
            parameters.Add("body=" + Uri.EscapeDataString(message.Body));
        }
        return "mailto:?" + string.Join("&", parameters);
    }

    private static string Recipients(IEnumerable<string>? addresses)
        => addresses is null
            ? string.Empty
            : string.Join(",", addresses.Where(a => !string.IsNullOrEmpty(a)).Select(Uri.EscapeDataString));
}

/// <summary>MAUI Essentials SMS on OpenHarmony (sms: over the startAbility bridge).</summary>
public sealed class OpenHarmonySms : ISms
{
    public static readonly OpenHarmonySms Instance = new();

    /// <summary>True when the ability bridge and the shell's startAbility sink are reachable.</summary>
    public bool IsComposeSupported => OpenHarmonyAbilityBridge.IsAvailable;

    public Task ComposeAsync(SmsMessage? message)
    {
        string uri = BuildSmsUri(message);
        if (!OpenHarmonyAbilityBridge.TryOpenUri(uri))
        {
            // The recipients/body are deliberately not logged (they are message content).
            OpenHarmonyBridge.WriteStatus("[maui] sms compose could not be dispatched");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The sms URI for <paramref name="message"/>: recipients first (comma-separated, unescaped
    /// because they are phone numbers), then the body as a single escaped query parameter. A null
    /// message yields "sms:" (the same "no target" shape as MAUI's compose-with-nothing paths).
    /// </summary>
    internal static string BuildSmsUri(SmsMessage? message)
    {
        message ??= new SmsMessage();
        string uri = "sms:";
        if (message.Recipients is { Count: > 0 })
        {
            uri += string.Join(",", message.Recipients.Where(r => !string.IsNullOrEmpty(r)));
        }
        if (!string.IsNullOrEmpty(message.Body))
        {
            uri += "?body=" + Uri.EscapeDataString(message.Body);
        }
        return uri;
    }
}

/// <summary>MAUI Essentials phone dialer on OpenHarmony (tel: over the startAbility bridge).</summary>
public sealed class OpenHarmonyPhoneDialer : IPhoneDialer
{
    public static readonly OpenHarmonyPhoneDialer Instance = new();

    /// <summary>True when the ability bridge and the shell's startAbility sink are reachable.</summary>
    public bool IsSupported => OpenHarmonyAbilityBridge.IsAvailable;

    /// <summary>
    /// Opens the dialer for <paramref name="number"/>. Validation matches MAUI's platform
    /// implementations (ArgumentNullException for null/empty/whitespace); an unavailable bridge
    /// is logged instead of thrown, exactly like the launcher.
    /// </summary>
    public void Open(string number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new ArgumentNullException(nameof(number));
        }
        string uri = "tel:" + number.Trim();
        if (!OpenHarmonyAbilityBridge.TryOpenUri(uri))
        {
            OpenHarmonyBridge.WriteStatus("[maui] phone dialer could not be dispatched");
        }
    }
}
