# Repo-Scoped Secrets

Local runtime credentials this repository's own processes read from the checkout. Each real file is ignored, and only its tracked `.example` and this catalog are committed. The convention is `GOVERNANCE.md` "Repo-Scoped Secrets".

| File | Holds | Read by |
| --- | --- | --- |
| `immich_api_key` | The Immich API key, as a single line | The `Trash DB` debug configuration in `.vscode/launch.json`, through `--apikey-file` |

To set one up, copy its `.example` to the name without the suffix and put the real value in it. The `/secrets/` path in the README usage examples is an example host path, and `/run/secrets/` in the workflow example is a Docker secret mount. Neither is this directory.
