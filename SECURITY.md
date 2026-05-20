# Security Policy

Sunder Agent packages can interact with model providers, local files, shell commands, web resources, MCP servers, execution targets, memory stores, and package-provided tools. Please report security issues privately.

## Reporting A Vulnerability

Use GitHub Security Advisories when possible:

https://github.com/Younics/sunder-agent-package/security/advisories/new

If GitHub Security Advisories are unavailable, contact the maintainers privately before publishing details.

Please include:

- Affected package, version, commit, provider, tool, or execution target.
- Operating system and environment.
- Reproduction steps or proof of concept.
- Impact assessment.
- Relevant logs with secrets, prompts, file contents, and credentials removed.

## Please Do Not Report Publicly

Do not open public issues for vulnerabilities involving API keys, provider credentials, local file access, shell execution, Docker execution, MCP tool access, package installation, memory leakage, transcript leakage, or prompt/context leakage.

## Supported Versions

Security fixes target the active development branch and maintained public package releases. If a fix changes package behavior, permissions, or Agent contracts, release notes will call that out explicitly.
