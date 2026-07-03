# Admin consent (work / school tenants)

OneDriveAsADrive signs in through Microsoft's own **Microsoft Graph Command Line Tools** public client — there's no app for you to register. But work/school (Entra ID) tenants often block users from consenting to apps on their own, and SharePoint mounts need broader Graph permissions. This page covers the one-time admin consent that clears both.

> ⚠️ **Check your organization's policy first.** Using a tool that rides Microsoft's first-party Graph client to reach your files may fall under your employer's acceptable-use or conditional-access rules. This project accesses only *your own* files with *your own* credentials — it doesn't bypass authentication or MFA — but if you don't own the tenant, clear it with IT/security before relying on it.

## When you'll need it

| Situation | Consent needed? |
|-----------|-----------------|
| Personal Microsoft account (`@outlook.com` etc.) | No — you self-consent |
| Work/school **OneDrive only**, tenant allows user consent | No |
| Work/school, tenant **blocks** user consent | Yes (one-time) |
| Any **SharePoint** mount (`Files.ReadWrite.All` + `Sites.Read.All`) | Usually yes (one-time) |

If you see **"Approval required"** instead of an **Accept** button on the first-run sign-in, your tenant needs admin consent.

## Scopes requested

- **OneDrive only** → `Files.ReadWrite` + `offline_access`
- **Any SharePoint mount** → `Files.ReadWrite.All` + `Sites.Read.All` + `offline_access`

The app only requests the broader SharePoint scopes when your `config.json` actually contains a SharePoint mount. There is no narrower scope that reaches shared SharePoint libraries **in this no-app-registration flow** — Graph does offer resource-scoped/selected permissions, but those require registering your own app, which this project deliberately avoids.

## Granting consent (Global Admin or Cloud Application Admin)

**Option A — direct URL.** Sign in as admin and click Accept:

```
https://login.microsoftonline.com/common/adminconsent?client_id=14d82eec-204b-4c2f-b7e8-296a70dab67e
```

**Option B — Entra portal.** Entra admin center → **Enterprise Applications** → **Microsoft Graph Command Line Tools** → **Permissions** → **Grant admin consent**.

After that one action, every user in the tenant can use OneDriveAsADrive with no further prompts.

## Security note on the Graph CLI client

The `14d82eec-…` client ID is Microsoft's own first-party public client, used by many Microsoft tools. Granting it admin consent authorizes *that Microsoft app* tenant-wide, not this project specifically — so it may already be consented in your tenant, and revoking it later affects other tools that use it too. Review with your security team if that scope of consent matters to you.
