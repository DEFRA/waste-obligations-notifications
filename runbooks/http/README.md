# HTTP requests

Use Rider's HTTP Client or the
[httpYac extension for VS Code](https://marketplace.visualstudio.com/items?itemName=anweber.vscode-httpyac)
on Windows, macOS or Linux. These files stay outside the .NET solution.

1. Copy `http-client.private.env.json.example` to `http-client.private.env.json`
   beside the requests. Git ignores the copy.
2. Fill in the host, token URL, client ID and client secret for your environment.
3. Open a `.http` file, select the environment and run an individual request.
   Requests acquire an OAuth token automatically.

- [Health](health.http)
- [Command DLQ](command-dlq.http): run readiness, then inspect before redrive or discard.
