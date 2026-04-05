using Google.Apis.Auth.OAuth2;
using ThreeDTilesLink.Core.Contracts;

namespace ThreeDTilesLink.Core.Auth
{
    internal sealed class DefaultCredentialAccessTokenProvider : IGoogleAccessTokenProvider
    {
        private static readonly string[] Scopes = ["https://www.googleapis.com/auth/cloud-platform"];

        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            GoogleCredential credential;
            try
            {
                credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Google SDK default credentials are not available. Run `gcloud auth application-default login` to configure them.",
                    ex);
            }

            if (credential.IsCreateScopedRequired)
            {
                credential = credential.CreateScoped(Scopes);
            }

            string token = await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(token) ? throw new InvalidOperationException("Google SDK default credentials returned an empty access token.") : token;
        }
    }
}
