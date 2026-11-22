# Project rules for the coding agent

## Lessons learned
- `python -m pip install <pkg>` fails here with "OSError: [WinError 2] ... Scripts\f2py.exe -> .deleteme"; installing with `--target .pylibs` (and `sys.path.insert(0, '.pylibs')` in the script) works. Remember to delete `.pylibs` afterwards so it doesn't pollute the repo. (2026-11-22)
