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

## The constraint that decides this

**Nine Lives is a free tool with no revenue behind it. There is no budget for a recurring
subscription.** That rules out the managed signing services regardless of how convenient they are,
and it is not a detail to be traded away because a monthly figure looks small — small recurring
costs on an unfunded project are exactly the ones that accumulate.

So the order below is by cost, and anything with a monthly fee is out.

## SignPath Foundation — free, and the route to take

SignPath's Foundation programme issues free code signing certificates to open source projects, and
runs the signing service too, so there is no private key to buy, store or protect. It is what a
number of OSS .NET projects use.

- **Cost:** free, subject to their review that the project qualifies as open source.
- **Effort:** apply to the Foundation programme, then define a signing policy and an artifact
  configuration, add `signpath/github-action-submit-signing-request@v2` to the release job, and
  store the API token as a repository secret.
- **Worth checking up front:**
  - **Artifact size.** `NineLives.exe` is ~146 MB, which is large for a signing submission. Confirm
    their limit before building the workflow around it. If it is a problem, signing the contents of
    the zip rather than the single-file exe is the usual way out.
  - **Certificate type.** A standard OV certificate removes the "unrecognised publisher" wording and
    shows a real name, but SmartScreen reputation still builds with download volume — so warnings
    may not vanish on day one. Ask them what they issue and set expectations from the answer rather
    than from this document.
  - **Turnaround.** Signing is a submit-and-poll round trip through their service, so the release
    job gets slower.

## If SignPath declines

**Certum Open Source Code Signing** is the cheap paid fallback — priced specifically for open
source, in the region of €25–30 per year rather than per month, on a hardware token. Verify current
pricing and terms directly; the figure moves. It is a real annual cost, so only worth considering if
the free route is closed.

## Ruled out

- **Azure Trusted Signing** — around $10/month. Convenient, and Microsoft's own CA, but a
  subscription this project cannot justify. Not an option unless that changes.
- **A traditional OV/EV certificate from a CA** — £200–400/year. Same objection, more work.

## Free things that help without a certificate

- **Build provenance attestation** — already shipping, see above.
- **Submitting the binary to Microsoft for analysis.** Developers can submit software through
  Microsoft's file submission portal to have a false positive reviewed. It does not put a publisher
  name on the exe, but it is free and can reduce warnings. Worth doing per release if SmartScreen
  proves stubborn.

## What still needs a human

Applying to the Foundation programme and verifying identity has to be done by the project owner.
That part cannot be automated or delegated. Once the account exists, wiring it up is a small PR:

```yaml
# after "Package release assets", before "Attest build provenance"
- name: Sign
  uses: signpath/github-action-submit-signing-request@v2
  with:
    api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
    organization-id: ${{ vars.SIGNPATH_ORGANIZATION_ID }}
    project-slug: nine-lives
    signing-policy-slug: release-signing
    # ...plus the artifact configuration and github-artifact-id inputs
```

Attestation should stay, and should run **after** signing so it covers the signed bytes. Repackage
the zip and regenerate `SHA256SUMS.txt` after signing too, or the published checksums will describe
the unsigned file.

Until then the README tells people plainly why the warning appears and how to verify the download
themselves.
