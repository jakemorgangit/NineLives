# Security policy

Nine Lives holds Azure SAS tokens, S3 access key pairs, SQL Server passwords and webhook URLs, and
it generates and runs T-SQL against instances you point it at — usually with rights to drop and
replace databases. Security reports are taken seriously here.

## Reporting a vulnerability

**Please don't open a public issue.** Use either:

- **[Private vulnerability reporting](https://github.com/jakemorgangit/NineLives/security/advisories/new)**
  — the Report a vulnerability button under the Security tab. Preferred, because the discussion and
  the fix stay in one place until there's something to release.
- **jake@blackcat.wales** if you'd rather use email.

Useful things to include: the version (the About screen), what an attacker would gain, and the smallest
set of steps that shows it. A proof of concept helps but is not required — a clear description of
the flaw is enough.

### What to expect

This is maintained by one person alongside client work, so:

- **Acknowledgement within 3 working days.** If you haven't heard back by then, please chase — it
  means the message went astray.
- An assessment, and either a fix or an explanation of why it isn't one, within 30 days.
- Credit in the release notes if you'd like it, or none if you'd rather not.

Please give a fix a reasonable chance to ship before disclosing publicly. If something is being
actively exploited, say so and it'll jump the queue.

## Supported versions

Only the **latest release** is supported. There are no maintenance branches — fixes go into the
next release. If you're running something older, upgrading is the fix.

## What the tool assumes

Worth stating plainly, because it determines whether a given finding is a bug or the design:

- **It runs locally as you.** It has whatever access your Windows account has, and no privilege
  boundary of its own.
- **Secrets live in Windows Credential Manager**, per-user, under four prefixes:
  `NineLives:Blob:*` (SAS tokens, and S3 access key pairs as `AccessKeyId:SecretKey`),
  `NineLives:SQL:*` (SQL Server passwords), `NineLives:Webhook:*` (one per configured endpoint)
  and `NineLives:WebhookProxy` (proxy credentials). A webhook URL counts as a secret here: the app
  never displays one back, and most carry their own token in the path. None are written to
  `config.json` and none appear in an exported config. Each goes to exactly one place and nowhere
  else: a SQL password to the instance it belongs to, a SAS token or S3 key pair to the storage
  endpoint it authenticates against, a webhook URL to the endpoint it addresses.
- **A generated script never contains a credential.** The server-side `CREDENTIAL` a restore from
  a URL needs is created on the instance separately, so the script you read before running it is
  safe to paste into a change ticket.
- **An Azure container can be reached without a stored secret at all.** Entra ID is supported —
  interactive sign-in, or `DefaultAzureCredential` for a managed identity or a service principal
  from the environment — and neither writes anything to Credential Manager. Worth saying here
  because the usual reason to read this file is to find out whether long-lived SAS tokens are
  compulsory. They are not. S3-compatible buckets authenticate with an access key pair, which is
  stored.
- **`config.json` is trusted input.** It sits in `%LOCALAPPDATA%\NineLives`, is plain text, and has
  no integrity checking. Anything able to write it is already running as you. That said, values
  read from it still get escaped properly before reaching T-SQL — someone who can write that file
  should not thereby get a cleaner path to running arbitrary SQL as sysadmin.
- **You choose the server.** The tool connects where you tell it to and runs the script you can
  read before you run it.

So: anything an attacker who is *already you, on your machine* can do is generally not a
vulnerability. Anything that leaks a secret somewhere it shouldn't go, sends it somewhere
unexpected, or turns untrusted data into executed T-SQL, is.

### Particularly interested in

- Any stored secret — a SAS token, an S3 secret key, a SQL password, a webhook URL — ending up
  anywhere other than Credential Manager: logs, the generated script, `config.json`, the exported
  config, the clipboard, an error message, a crash dump.
- Anything that reaches T-SQL without going through `src/NineLives.Core/Services/TSql.cs`, or a way past its quoting.
- A restore being aimed at a server or database other than the one shown in the confirmation.
- Credentials being sent over a connection that isn't validated the way the settings claim.

### Known and already tracked

Not vulnerabilities to report — they're on the list:

- The released binary is **unsigned**, so SmartScreen warns on download
  ([#33](https://github.com/jakemorgangit/NineLives/issues/33)). Releases carry a
  [build provenance attestation](https://github.com/jakemorgangit/NineLives#windows-protected-your-pc)
  you can verify in the meantime.
- `TrustServerCertificate` defaults to true
  ([#17](https://github.com/jakemorgangit/NineLives/issues/17)).

## Thank you

Genuinely — a private report is a favour, and it's the difference between fixing something quietly
and finding out about it the hard way.
