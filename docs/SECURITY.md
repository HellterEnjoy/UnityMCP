# Security

## Localhost Only

The Unity bridge is designed to listen on:

```text
http://127.0.0.1:8765
```

It is intentionally local-only. Do not expose it on a public or shared network interface without
authentication and tighter command restrictions.

## Why This Matters

The bridge can:

- inspect editor state
- modify scene objects and components
- enter Play Mode
- run tests
- drive parts of the Unity editor

That is powerful enough that a public bridge without access control would be a bad idea.

## Current Security Model

- local bridge
- intended for trusted local development environments
- no built-in remote auth layer

## Practical Rule

If you need remote or multi-user access later, add a real auth and permission model first. Do not
solve that by simply binding the bridge to a non-localhost address.
