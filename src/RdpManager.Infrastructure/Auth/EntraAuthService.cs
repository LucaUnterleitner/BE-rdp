using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using RdpManager.Core.Models;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.Infrastructure.Auth;

/// <summary>Sign-in state shown in the UI. Contains no tokens.</summary>
public sealed record AuthState(bool SignedIn, string? Username = null, string? DisplayName = null, IReadOnlyList<string>? Roles = null, string? Error = null)
{
    public static readonly AuthState SignedOut = new(false);
}

public interface IAuthService
{
    bool Configured { get; }
    bool RequireSignIn { get; }
    AuthState State { get; }
    event Action<AuthState>? StateChanged;
    Task<AuthState> SignInSilentAsync(CancellationToken ct = default);
    Task<AuthState> SignInInteractiveAsync(CancellationToken ct = default);
    Task SignOutAsync();
    /// <summary>Access token for later Microsoft Graph or backend API calls. Never log the result.</summary>
    Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken ct = default);
    bool HasRequiredRole(AuthState state);
}

/// <summary>
/// Microsoft Entra ID sign-in with MSAL.NET as a public client (no secret). Uses the Windows broker (WAM) when
/// enabled, which keeps refresh tokens in the operating system; without the broker the token cache stays in
/// memory only. Single-tenant authority from the policy. MSAL logging runs with PII logging switched off.
/// </summary>
public sealed class EntraAuthService(EntraConfig? config, Func<IntPtr> parentWindow) : IAuthService
{
    private IPublicClientApplication? _app;
    private AuthState _state = AuthState.SignedOut;

    public bool Configured => config is not null;
    public bool RequireSignIn => config?.RequireSignIn == true;
    public AuthState State => _state;
    public event Action<AuthState>? StateChanged;

    private IPublicClientApplication App()
    {
        if (_app is not null) return _app;
        var builder = PublicClientApplicationBuilder.Create(config!.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, config.TenantId)
            .WithClientName("BearingPoint Remote Desktop")
            .WithLogging((level, message, containsPii) =>
            {
                if (!containsPii) DiagnosticLog.Info($"MSAL {level}: {message}");
            }, LogLevel.Warning, enablePiiLogging: false, enableDefaultPlatformLogging: false);
        if (config.UseBroker)
        {
            builder = builder
                .WithParentActivityOrWindow(parentWindow)
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "BearingPoint Remote Desktop" });
        }
        else
        {
            builder = builder.WithDefaultRedirectUri();
        }
        _app = builder.Build();
        return _app;
    }

    private IReadOnlyList<string> Scopes => config!.Scopes;

    public async Task<AuthState> SignInSilentAsync(CancellationToken ct = default)
    {
        if (!Configured) return _state;
        try
        {
            var app = App();
            var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
            AuthenticationResult result;
            if (account is not null) result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
            else if (config!.UseBroker) result = await app.AcquireTokenSilent(Scopes, PublicClientApplication.OperatingSystemAccount).ExecuteAsync(ct).ConfigureAwait(false);
            else return SetState(AuthState.SignedOut);
            return SetState(FromResult(result));
        }
        catch (MsalUiRequiredException)
        {
            return SetState(AuthState.SignedOut);
        }
        catch (MsalException e)
        {
            DiagnosticLog.Warn($"Silent sign-in failed ({e.ErrorCode})");
            return SetState(new AuthState(false, Error: Friendly(e)));
        }
    }

    public async Task<AuthState> SignInInteractiveAsync(CancellationToken ct = default)
    {
        if (!Configured) return _state;
        try
        {
            var app = App();
            var result = await app.AcquireTokenInteractive(Scopes)
                .WithParentActivityOrWindow(parentWindow())
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(ct).ConfigureAwait(false);
            return SetState(FromResult(result));
        }
        catch (MsalClientException e) when (e.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            return SetState(new AuthState(false, Error: "Sign-in was cancelled."));
        }
        catch (MsalException e)
        {
            DiagnosticLog.Warn($"Interactive sign-in failed ({e.ErrorCode})");
            return SetState(new AuthState(false, Error: Friendly(e)));
        }
    }

    public async Task SignOutAsync()
    {
        if (_app is not null)
        {
            try
            {
                foreach (var a in await _app.GetAccountsAsync().ConfigureAwait(false)) await _app.RemoveAsync(a).ConfigureAwait(false);
            }
            catch (MsalException e) { DiagnosticLog.Warn($"Sign-out failed ({e.ErrorCode})"); }
        }
        SetState(AuthState.SignedOut);
    }

    public async Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken ct = default)
    {
        if (!Configured) throw new InvalidOperationException("Sign-in with Microsoft Entra ID is not configured.");
        var app = App();
        var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault()
            ?? (config!.UseBroker ? PublicClientApplication.OperatingSystemAccount : throw new InvalidOperationException("Sign in first."));
        var result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
        return result.AccessToken;
    }

    public bool HasRequiredRole(AuthState state)
        => config is null || config.AllowedRoles.Count == 0 || (state.Roles ?? []).Any(r => config.AllowedRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

    private static AuthState FromResult(AuthenticationResult r)
    {
        var roles = r.ClaimsPrincipal?.FindAll("roles").Select(c => c.Value).ToList() ?? [];
        var name = r.ClaimsPrincipal?.FindFirst("name")?.Value;
        return new AuthState(true, r.Account?.Username, name, roles);
    }

    private AuthState SetState(AuthState s)
    {
        _state = s;
        StateChanged?.Invoke(s);
        return s;
    }

    private static string Friendly(MsalException e) => e switch
    {
        MsalServiceException { ErrorCode: "invalid_client" or "unauthorized_client" } => "The app registration in Microsoft Entra ID is not set up for this app. Contact IT.",
        MsalServiceException => "Microsoft Entra ID rejected the sign-in. Try again or contact IT.",
        MsalClientException { ErrorCode: "network_not_available" or "request_timeout" } => "Microsoft Entra ID cannot be reached. Check the network connection.",
        _ => "Sign-in with Microsoft Entra ID did not work. Try again or contact IT.",
    };
}
