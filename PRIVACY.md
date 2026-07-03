# Privacy

OneDriveAsADrive collects **nothing**.

- **No telemetry, no analytics, no crash reporting.** The app phones home to no one — not even us.
- **Your files and credentials never leave your machine except to go to Microsoft.** The app talks to exactly one remote endpoint: the official Microsoft Graph API (`graph.microsoft.com`), authenticated with your own Microsoft account via the Windows account broker (WAM). File contents stream directly between your machine and OneDrive/SharePoint.
- **Authentication tokens** are handled by Microsoft's MSAL library and the Windows broker; this app never sees or stores your password. Tokens are cached by Windows, not by us.
- **Local data:** a per-install random secret, an optional `config.json` you write yourself, and a plain-text log file — all under `%LOCALAPPDATA%\OneDriveAsADrive\`. These are removed on uninstall when it runs in your own user profile (the normal case, or `npx onedriveasadrive uninstall`); see the README Uninstall note for the separate-admin edge case.
- **The installer** downloads release binaries from GitHub and verifies their SHA256. GitHub sees the download request, as with any GitHub-hosted software.

That's the whole story. The code is MIT-licensed and small enough to audit over coffee: [github.com/Avatorsinc/OneDriveAsADrive](https://github.com/Avatorsinc/OneDriveAsADrive).
