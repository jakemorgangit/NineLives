# Code signing

Tracking issue: [#33](https://github.com/jakemorgangit/NineLives/issues/33).

`NineLives.exe` is unsigned. Every person who downloads it meets **"Windows protected your PC —
Microsoft Defender SmartScreen prevented an unrecognised app from starting"** and has to click
through *More info → Run anyway*.

For most tools that is a papercut. For this one it is the first impression made by software that
asks to be pointed at a production SQL Server with rights to drop and replace databases. A fair
number of DBAs will stop there, and plenty of corporate policies will stop for them.

## What is already in place

Releases carry a **Sigstore build provenance attestation**, added in
`.github/workflows/release.yml`. It is cryptographic proof that the exe on the release page was
built by this repo's release workflow from a named commit:

```powershell
gh attestation verify NineLives.exe --repo jakemorgangit/NineLives
```

That is a real guarantee and it costs nothing. **It does not touch SmartScreen.** Windows does not
consult Sigstore; it looks for an Authenticode signature. Provenance answers "did this come from
the source I think it did", which is the question a careful reviewer asks. SmartScreen asks "does
Windows recognise the publisher", and only a certificate answers that.

## Options for the certificate

### SignPath — free for open source

The route the issue proposed, and what several OSS .NET projects use. SignPath issues the
certificate; there is nothing to buy or store.

- **Cost:** free, subject to their OSS eligibility review.
- **Effort:** register the project, define a signing policy and an artifact configuration, add
  `signpath/github-action-submit-signing-request@v2` to the release job, store the API token as a
  repository secret.
- **Catch:** signing is a submit-and-poll round trip through their service, so the release job gets
  slower. Worth confirming their upload size limit up front — `NineLives.exe` is ~166 MB, which is
  large for a signing submission.

### Azure Trusted Signing — ~$10/month

Microsoft's managed signing service. Short-lived certificates, no private key to look after, and
because it is Microsoft's own CA it carries SmartScreen reputation well.

- **Cost:** Basic tier, around $9.99/month.
- **Effort:** likely the least of the three — there is already an Azure tenant here, and
  `azure/trusted-signing-action` is a single step.
- **Catch:** identity validation, and eligibility rules that have included a minimum trading
  history for organisations. **Check whether Blackcat Data Solutions Ltd qualifies before
  committing to this route** — that is the deciding factor, not the price.

### A traditional OV certificate — £200–400/year

Buy from a CA, keep it on a hardware token or in a KMS. Full control, most work, and an OV
certificate builds SmartScreen reputation slowly by download volume. Hard to recommend over the
other two.

## Recommendation

Try **Azure Trusted Signing** first — the tenant already exists, the wiring is one action, and it
gives the best SmartScreen outcome. Fall back to **SignPath** if the eligibility check fails.

## What still needs a human

Both routes need an account created and an identity verified by the project owner. That part
cannot be automated or delegated. Once the account exists, wiring it up is a small PR:

```yaml
# after "Package release assets", before "Attest build provenance"
- name: Sign
  uses: azure/trusted-signing-action@v0   # or signpath/github-action-submit-signing-request@v2
  with:
    files-folder: releases
    files-folder-filter: exe
    # ...plus the account/profile inputs from the service
```

Attestation should stay, and should run **after** signing so it covers the signed bytes. Repackage
the zip and regenerate `SHA256SUMS.txt` after signing too, or the published checksums will describe
the unsigned file.

Until then the README tells people plainly why the warning appears and how to verify the download
themselves.
