# Novalyth OpenSim

This repository is the Novalyth OpenSimulator fork.

## Remotes

- `upstream`: official OpenSimulator source
- `origin`: Novalyth OpenSim repository

## Main development branch

`novalyth-main`

## Shared server workflow

```bash
novalyth-git-status
novalyth-save "NOVALYTH: describe the change"
```

The workflow is:

1. local unprivileged Release build
2. commit
3. isolated second Release build as `novalythgit`
4. automatic push to GitHub
5. GitHub Actions Release build

Runtime configuration, database credentials, private SSH keys, caches,
logs and production data are kept outside this source repository.
