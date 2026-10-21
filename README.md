This is a fork that allows the AIPanel command to start pi on your local machine and use it as the agent to complete tasks in Rhino. You must have pi installed and configured with a provider/models before you can use it. This is a 100% AI generated addition on top of the RhinoAI plugin, I have done no code review an have no understanding of its structure or changes. I have tested this on Rhino 9 WIP and it works well, use at your own risk. Note that you could just connect to the MCP server from pi and ignore this entire plugin, but I wanted to see what was possible. 

<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="art/logo-dark.svg">
  <img alt="Rhino MCP" src="art/logo.svg" width="180">
</picture>

# RhinoAI

**An AI Integration for Rhino3d**

[**Read the docs →**](https://mcneel.github.io/RhinoAI/docs/)

</div>

---

# Documentation

Full guides live at **[mcneel.github.io/RhinoAI](https://mcneel.github.io/RhinoAI/docs/)**:

- [Getting Started](https://mcneel.github.io/RhinoAI/docs/getting-started/) - Install the plugin and wire up an AI assistant (Claude Desktop, Claude Code, GitHub Copilot, OpenAI Codex, Gemini CLI, or a local model).
- [Try It Out](https://mcneel.github.io/RhinoAI/docs/try-it-out/) - Confirm everything works with a first prompt, then browse the recipes and examples.
- [Advanced](https://mcneel.github.io/RhinoAI/docs/advanced/) - Advanced workflows
- [Developers](https://mcneel.github.io/RhinoAI/docs/developers/) - Use the Rhino MCP in your development cycle.

# Quick start

The fastest path is [Claude Desktop](https://mcneel.github.io/RhinoAI/docs/getting-started/connector/): install the [Rhino3d connector](https://github.com/mcneel/RhinoAI/releases/download/connector-v0.1.6/connector.mcpb) and let it install the Rhino plugin for you. For other assistants, see [Getting Started](https://mcneel.github.io/RhinoAI/docs/getting-started/).

# Building & Debugging

Use **Run and Debug** from within VSCode to build, launch Rhino, and start the MCP Server all in one click.

# Getting Help

Ask questions, post discussions and ideas to the [Rhino Discourse forums](https://discourse.mcneel.com/c/rhino/artificial-intelligence-rhino/162), or [open an issue](https://github.com/mcneel/RhinoAI/issues).
