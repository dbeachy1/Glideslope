# Agent instructions for Glideslope

## Commit attribution

The lead reviewer prepares the accepted commit message. End every new commit message with one
`Co-authored-by:` trailer naming the highest-level model that participated in the design, coordination, or
code review for the work represented by that commit, using its actual model name and version (for example,
Astra 6 or Sol 6). Attribute the model responsible for the engineering sign-off, not the implementation worker.
Never list Luna as a co-author. Keep review-model, effort, and baseline trailers accurate.

## Human maintainability

Organize code by responsibility, using the existing project structure where appropriate. Do not impose arbitrary
file or method size limits or split cohesive code merely to reduce its line count. Use comments judiciously to
explain non-obvious purpose and invariants. Keep comments focused on current behavior and non-obvious constraints; remove obsolete historical commentary.

## Source comments and publication checks

Use a senior engineer's judgment: explain non-obvious purpose, constraints, and invariants; do not narrate obvious
operations or the editing process. Write concise comments about current behavior. Remove obsolete draft notes,
superseded implementations, correction narratives, and lengthy chronological explanations from source comments.
Preserve a still-relevant technical constraint as a short explanation of why the current code needs it.

Before making a repository public, review comments across all intended source files for this obsolete or excessive
material, and scan intended source, documentation, Git history and metadata, and release artifacts for personal
information and credentials. Resolve findings before publication. A file-size or secret scan alone does not satisfy
this comment and privacy review.

## Public repository boundary

This public-facing Glideslope repository was created from an explicit allowlist of current files in a fresh root
commit to leave deleted files and earlier private history behind. Keep the original repository and its full history
private as an archive. Do not import old commits, branches, tags, Git objects, or deleted-file blobs, or use a fork,
mirror, or clone-and-delete workflow.

This repository is public. Before each commit and push, review the exact staged files, diff, generated artifacts,
and commit metadata for private personal information or credentials. Do not commit private contact details, nonpublic
account identifiers, tokens, keys, real user-specific paths, private machine identifiers, personal logs, or unrelated
desktop content. Doug's intentionally public contact email is allowed in commits and documentation. Screenshots of
the Glideslope app itself are allowed, including its displayed usage figures. Check that screenshots do not expose
credentials or unrelated personal content. Doug approved the four original README screenshots. Apply the same check
to release assets before uploading them.
