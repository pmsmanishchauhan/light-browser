# SignPath Foundation application (draft)

Apply at https://signpath.org/ ("Open source" / "Apply for free code signing"). The requirements below are from
memory of their public terms; read the current ones on their site before sending, and change this text to match.

## Answers to paste

- **Project name:** LightBrowser
- **Repository:** https://github.com/pmsmanishchauhan/light-browser
- **License:** MIT (`LICENSE` in the repository root)
- **What it is:** A lightweight Windows browser (WPF, .NET 8, Microsoft Edge WebView2) that keeps a separate
  sign-in for every client account, for people who manage many accounts on the same websites from low-end PCs.
- **Why signing:** The releases are single `.exe` files that people download from GitHub. Without a signature
  Windows SmartScreen warns on every new release.
- **Build:** `.github/workflows/build-release.yml` on GitHub-hosted runners; the signing request is made from
  that workflow, and `.signpath/artifact-configuration.xml` lists the two exes to sign.
- **Releases so far:** v0.1.0 to v0.4.2 on GitHub Releases.
- **Maintainers / approvers:** pmsmanishchauhan (add the real name and a contact e-mail address).
- **No malware, no unwanted software:** the app sends nothing to the author. It contacts the sites you open,
  and GitHub only when you press "check for updates" (see "Privacy" in the README).

## Things they may ask for

- A code signing policy statement on the project page: the README section "Privacy and code signing" covers it
  (remove the word "planned" once the certificate is issued).
- A privacy statement: same README section.
- That every signed build comes from the public repository: the workflow does this.
- Two-factor authentication on the GitHub account that owns the repository: turn it on if it is not.

## After approval

1. In SignPath, create the project with the slug you want and a signing policy (for example `release-signing`);
   upload `.signpath/artifact-configuration.xml` as the artifact configuration.
2. Install the SignPath GitHub App / connector as the instructions say, and create an API token.
3. In the repository settings add the variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`,
   `SIGNPATH_POLICY_SLUG` and the secret `SIGNPATH_API_TOKEN`.
4. Run the workflow ("Build release"), approve the request in SignPath if the policy asks, download the `signed`
   artifact and attach it: `gh release create vX.Y.Z signed/LightBrowser-small.exe signed/LightBrowser-standalone.exe ...`
5. Check one exe with `Get-AuthenticodeSignature`, and say so in the release notes.
