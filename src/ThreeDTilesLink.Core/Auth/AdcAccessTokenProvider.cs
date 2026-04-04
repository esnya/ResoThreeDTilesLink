using Google.Apis.Auth.OAuth2;
using System.IO;
using ThreeDTilesLink.Core.Contracts;

namespace ThreeDTilesLink.Core.Auth;

public sealed class AdcAccessTokenProvider : IGoogleAccessTokenProvider
{
    private static readonly string[] Scopes = ["https://www.googleapis.com/auth/cloud-platform"];

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        GoogleCredential? credential = null;
        try
        {
            credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
        }
        catch
        {
            // Fallback to explicit well-known paths when automatic ADC discovery fails.
        }

        credential ??= await TryLoadFromWellKnownPathsAsync(cancellationToken);
        if (credential is null)
        {
            throw new InvalidOperationException(
                "ADC credentials are not available. Run `gcloud auth application-default login` or set GOOGLE_APPLICATION_CREDENTIALS.");
        }

        if (credential.IsCreateScopedRequired)
        {
            credential = credential.CreateScoped(Scopes);
        }

        var token = await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("ADC returned an empty access token.");
        }

        return token;
    }

    private static async Task<GoogleCredential?> TryLoadFromWellKnownPathsAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<string>();

        var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            candidates.Add(envPath);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, ".config", "gcloud", "application_default_credentials.json"));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            candidates.Add(Path.Combine(appData, "gcloud", "application_default_credentials.json"));
        }

        foreach (var path in candidates.Where(File.Exists))
        {
            try
            {
#pragma warning disable CS0618
                return await GoogleCredential.FromFileAsync(path, cancellationToken);
#pragma warning restore CS0618
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }
}
