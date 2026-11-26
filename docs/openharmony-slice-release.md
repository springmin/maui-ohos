# OpenHarmony MAUI slice — fork publication notes

This file records how the OpenHarmony slice is published from the `springmin/maui-ohos` fork.
It is intentionally separate from `README-openharmony-slice.md`, which describes the upstream
platform code and must not depend on a fork's branch/tag/release layout.

## Branch and source drop

| Item | Value |
|---|---|
| Branch | `feature/openharmony` |
| Tag / asset | `ohos-slice-1.0.1` → `ohos-slice-1.0.1.tar.gz` + `.sha256` |
| Directory | `src/Core/src/Platform/OpenHarmony/` |

Clone the slice branch (the default branch does not carry the slice):

```sh
git clone --depth 1 --branch feature/openharmony https://github.com/springmin/maui-ohos.git maui-ohos
test -f maui-ohos/src/Core/src/Platform/OpenHarmony/OpenHarmonyMauiAppHost.cs && echo "slice present"
```

Or take the tarball (same tree, prefixed `ohos-slice-1.0.1/`):

```sh
curl -LO https://github.com/springmin/maui-ohos/releases/download/ohos-slice-1.0.1/ohos-slice-1.0.1.tar.gz
tar xzf ohos-slice-1.0.1.tar.gz
test -f ohos-slice-1.0.1/src/Core/src/Platform/OpenHarmony/OpenHarmonyMauiAppHost.cs && echo "slice present"
```

## Why the fork's `main` is not fast-forwarded to the slice

The fork's `main` mirrors `dotnet/maui` at commit `1cd2e15b`. `feature/openharmony` descends
from it, but the branch's first slice commit removed the rest of the upstream tree, so
fast-forwarding `main` would delete thousands of upstream files. The slice is therefore
published as a branch + tag + tarball, with a pointer from the fork's default branch.
