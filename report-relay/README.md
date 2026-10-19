# TimePlayer report relay

Report a problem (Control+Shift+B) and Suggest a feature (Control+Shift+F) in TimePlayer send here. This small Cloudflare Worker files each report
as an issue in the private repository **ernie96/TimePlayer-reports**, so TimePlayer users need no account,
and their logs aren't public. Logs longer than about 50,000 characters are saved as files in that repository's
`logs` folder and linked from the issue.

Limits: 3 reports a minute from one internet address, 5,000 characters of description, 400,000 of log.

## One-time setup

1. **GitHub token.** On GitHub: Settings, Developer settings, Personal access tokens, Fine-grained tokens,
   Generate new token. Name it "TimePlayer reports". Repository access: Only select repositories,
   **ernie96/TimePlayer-reports**. Permissions: **Issues: Read and write**, **Contents: Read and write**.
   Copy the token.
2. **Cloudflare account** (free): sign up at dash.cloudflare.com. When asked, pick your workers.dev
   subdomain (for example `ernie96`).
3. **Deploy**, from this folder:

   ```
   npx wrangler login
   npx wrangler deploy
   npx wrangler secret put GITHUB_TOKEN
   ```

   Paste the token when asked. `deploy` prints the address, such as
   `https://timeplayer-reports.ernie96.workers.dev`.
4. If that address differs from `RelayUrl` in `src/TimePlayer.Core/Community/BugReports.cs`, change it there
   (keep `/report` at the end).

## Reading reports

```
gh issue list --repo ernie96/TimePlayer-reports
gh issue list --repo ernie96/TimePlayer-reports --label "feature request"
gh issue view 12 --repo ernie96/TimePlayer-reports
```
