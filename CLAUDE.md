# Working on AppCenter

## internal/ is private

The `internal/` folder holds local working notes. It is gitignored and it stays
that way. Nothing in it is documentation, and none of it is for publication.

Do not let its contents reach any tracked file. That means:

- no links or paths pointing into `internal/` from README, docs, or code
- no summarising, paraphrasing, or quoting what the notes say — a sentence
  sourced from the notes leaks the same information as the notes themselves
- no describing what the folder holds — with one deliberate exception, the
  comment above the `internal/` entry in `.gitignore`. That the folder exists and
  holds working notes is not what is being protected; the contents are. It stays
  as it is: do not extend it, and do not "fix" it back to a bare entry
- no referencing it in commit messages or PR descriptions

Read the notes freely when working — that is what they are for. Write research,
rationale, and planning into them rather than into tracked files. Anything that
does ship in the README or in a code comment has to stand on its own, making its
point without pointing at the notes.

If a change seems to need context that only exists in `internal/`, write that
context out properly for a public audience instead of citing the notes.

## Never commit or push unasked

Do not run `git commit` or `git push` unless you have been told to. Finishing a
change is not permission to commit it, and being told to commit is not
permission to push. Leave the work in the working tree and say what you changed.

Asking is fine and often welcome — when a change is at a natural stopping point,
offer to commit it. Wait for the answer. Permission given once covers that
commit, not the next one.
