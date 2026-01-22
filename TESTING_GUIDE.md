# TestSprite Testing Guide for Dynamic AI

This guide explains how to use the **TestSprite MCP** to automate testing and debugging within the Dynamic AI project.

## 🚀 Getting Started

The TestSprite MCP server is now configured in your `mcp_config.json`. This allows your AI assistant to access TestSprite's intelligent testing tools.

## 🛠 Features Enabled

- **Automated Test Generation:** Automatically create unit and integration tests for your C# services.
- **AI-Powered Debugging:** If a test fails, TestSprite can help identify the root cause by analyzing the code vs. the intent.
- **Quality Assurance:** Validate that generated SQL queries meet the business logic requirements.

## 🤖 How to use via MCP (AI assistant)

Once the MCP server is activated in your environment, you can use specialized tools. As an AI, I can call these tools for you. If you don't see the tools yet, you may need to **Restart your AI Assistant** or **Refresh MCP Servers**.

### Available MCP Tools (Expected):
- `testsprite_generate_code_summary`: Scans the project and creates a JSON summary.
- `testsprite_generate_tests`: Generates unit tests for specific files.
- `testsprite_run_tests`: Executes tests and provides AI-driven debugging.

### Usage Examples (for you to tell me):
- *"Scan the DynamicAi project using the TestSprite MCP tool."*
- *"Generate tests for IntentClassificationService.cs using the TestSprite tool."*

## 💻 Manual Use (Terminal)

If the MCP is not yet active in your UI, you can always use the terminal fallback:
```bash
npx @testsprite/testsprite-mcp@latest --help
```

## ⚠️ Security Note

Your API key is stored securely in `mcp_config.json`. Do not commit this file to public repositories or share the key in logs.
