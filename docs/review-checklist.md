# Review checklist

For anyone reviewing a change to Relay: a person, or an agent asked to review in a room. It exists
because a review that only reads the diff for what it says tends to miss what the change took away.

## Before you start

- **Work from the change and the issue, not from the author's account of it.** Read the diff and
  the issue's acceptance criteria first, and form your own view. Read the author's summary and
  their list of doubts afterwards; a review that starts from that list tends to stop at it.
- **Check out the branch and run it.** `dotnet build` must have no warnings and `dotnet test`
  must pass. A claim you ran is worth more than one you reasoned.

## What to look for

Go through these in order. Defects come before tests and wording.

1. **What did the old code guarantee that the new code does not?** Include what it guaranteed by
   accident. Replacing a file by rename instead of rewriting it in place, for example, changes its
   permissions, what happens to a symlink, who is locked out, and what is left behind on failure.
   For each line removed or replaced, ask what else it did.
2. **What is left behind when each step fails?** For every statement that can throw or be
   interrupted: which files, processes, locks or partly updated state remain, and does the next
   run cope with them?
3. **Does it hold on every operating system Relay supports?** Wherever the change touches files,
   paths, processes, signals or the console, think through Windows, macOS and Linux separately:
   file sharing and locking, antivirus and indexers that open new files, permissions, symlinks,
   line endings, case sensitivity, `cmd.exe` shims.
4. **Who else touches this at the same time?** Another Relay process, an older version of Relay,
   an agent CLI, the user's editor.
5. **Does it meet every acceptance criterion in the issue?** Take them one by one and say which
   test or which line meets each. "Where feasible" is not met by silence: say what was not done.
6. **Does it keep to `docs/threat-model.md` and the security rules in `AGENTS.md`?** If the change
   is covered by the threat model, the threat model changes in the same pull request.
7. **Do the tests cover failure, not only success?** A test that cannot fail when the behaviour
   is broken is not coverage. For a fix, check that the test fails without it.
8. **Are the README, `AGENTS.md` and comments still true?**

## How to write a finding

Each finding says:

- **Where:** file and line.
- **What goes wrong:** a concrete scenario. Who does what, and what happens. "This could be
  fragile" is not a finding.
- **Verified or reasoned:** whether you made it happen, or worked it out from the code. Both are
  welcome; say which.
- **How serious:** must fix before merging, or optional.

Put findings on the pull request, on the lines they are about where you can.

## Answering a review

- **Every finding gets an answer on the pull request:** fixed, with the commit, or not fixed, with
  the reason. A finding that is set aside is recorded there. It is never just dropped, and a doubt
  the author already had is not settled by the reviewer agreeing in passing.
- A risk that is accepted and that a user could meet belongs in `docs/threat-model.md` or the
  README's limitations, not only in the pull request.
- The reviewer checks the answers, and says plainly whether anything that must be fixed is left.

## When is it ready?

- Nothing that must be fixed is open.
- Every finding has an answer.
- CI has **finished** and passed on all three operating systems. A pull request whose checks are
  still running is not ready, whatever was run locally.
