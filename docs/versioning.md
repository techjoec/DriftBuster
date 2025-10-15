# Version Management

All component versions live in `versions.json`.  The file tracks the
independent version numbers for the Python core package, HTML catalog
metadata, GUI release, PowerShell module, and each format plugin.  Adjust the
values that changed and run:

```bash
python scripts/sync_versions.py
```

The script propagates the new numbers into the build props, manifests, docs,
and tests.  If you attempt to bump a component that is not represented in the
current source tree, the script exits with an error so you can correct the
inputs before committing.

The separation allows the GUI or PowerShell module to ship a new version
without touching the format plugins, and vice versa.  Only bump the entries
whose implementation actually changed.
