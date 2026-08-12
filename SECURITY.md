# Security policy

Please report suspected vulnerabilities privately to the repository maintainers. Do not attach real ETL, exported traces, credentials, tokens, private keys, organization UNC paths, or logs containing personal data to a public issue.

Provide a minimal reproduction with synthetic paths and data, the affected version, expected impact, and suggested mitigation if known. Maintainers should acknowledge a report promptly, reproduce it on an isolated system, and coordinate disclosure after a fix is available.

WprHelper treats target arguments as data and never runs them through a shell. Elevated IPC accepts only strict capture commands over a unique current-user named pipe. A trace may still contain sensitive system activity; handle it as confidential diagnostic data.
