# Slipgates MCP server

Gives an agent session the old agent-ha-bridge tool names (`list_agent_sessions`,
`read_agent_session`, `reply_to_agent_session`, `launch_agent_session`, `read_launch_result`,
`get_launch_options`) over the Slipgates host's local pipe, on this machine. Protocol and
environment: [local-pipe.md](../docs/protocol/local-pipe.md#the-mcp-server-mcp).

```
cd mcp && npm ci && npm test
node src/server.mjs        # stdio; started by the agent as a child of its session
```

- No host: the server lists no tools, so agents fall back. A host that rejects the credential fails the connection; with no credential only the read tools are offered.
- Connected: its instructions tell agents to prefer these tools for messaging other sessions.
- Replies and launches are refused by the host until agent scopes allow them (AP4/AP5); the
  server reports that as `outcome: "refused"`.
- Not in this slice: the read-only HTTP mode, `ask_via_home_assistant`, other machines.
