# ServiceNow Desk

A Windows desktop app for client services teams who need to create, update, resolve, and search ServiceNow incidents and requests without waiting on the web UI. The menus stay on screen. The app sends the Table API and Service Catalog API calls itself.

## What you can do

- Incidents: create, update, assign, add work notes or customer comments, and resolve with a close code and close notes.
- Requests (`sc_request`): create a direct request, update it, and close it.
- Request items (`sc_req_item`): update, assign, and close the items agents actually fulfill. Open them from the request they belong to.
- Catalog orders: search the service catalog, fill variables, and order an item for a caller. This is the path that runs the normal catalog workflow.
- Search: one box searches the current list. The Search page looks across incidents, requests, and items at once, including closed records.

Practice data is built in, so the team can learn the layout before an instance is connected. Nothing in practice mode is sent to ServiceNow.

## Keyboard

| Shortcut | Action |
| --- | --- |
| Ctrl+1 … Ctrl+6 | Incidents, Requests, Request items, Search, Order catalog, Connection |
| Ctrl+K or Ctrl+F | Focus search |
| Ctrl+N | New incident or request |
| Ctrl+S | Save |
| Ctrl+Enter | Post the note, or confirm resolve when that panel is open |
| Ctrl+Shift+R | Resolve / close |
| F5 | Refresh the current list |
| Esc | Close the resolve panel |

Type a person's name in Caller, Requested for, Assigned to, or Assignment group. The match list opens under the field. Enter accepts the highlighted person.

## Connect to your instance

1. Open **Connection**.
2. Enter the instance URL, for example `https://company.service-now.com`.
3. Choose a sign-in method.
   - **Username and password** is the fastest way to start. Sign in as the agent. "My open" and "My groups" then mean that person.
   - **OAuth password grant** uses an OAuth client plus the agent's user name and password.
   - **OAuth client credentials** uses the integration user configured on the OAuth app. "My open" is that integration user, not the person at the keyboard.
4. Click **Connect**.

The password and client secret are stored with Windows DPAPI for the current Windows user, under `%AppData%\ServiceNowDesk\settings.json`. They are not written in plain text.

### OAuth app

In ServiceNow: **System OAuth > Application Registry > New > Create an OAuth API endpoint for external clients**. Copy the client ID and client secret. For client credentials, set the OAuth application user on that registry record.

### Access the account needs

The signed-in user needs the same rights they already use in the web UI, typically `itil`, plus permission to:

- read and write `incident`, `sc_request`, and `sc_req_item`
- read `sys_user`, `sys_user_group`, `sys_choice`, and `sys_journal_field`
- order from the service catalog if you use **Order catalog**

If an update is blocked, the red banner shows the message ServiceNow returned.

### How updates are sent

The app uses the Table API:

- `GET/POST/PATCH /api/now/table/incident`
- `GET/POST/PATCH /api/now/table/sc_request`
- `GET/PATCH /api/now/table/sc_req_item`
- journal notes are `work_notes` or `comments` on that record
- choices, users, and groups come from the matching tables

Catalog ordering uses `POST /api/sn_sc/servicecatalog/items/{sys_id}/order_now`.

Search text is sent with ServiceNow's text index operator (`123TEXTQUERY321`). Ticket numbers such as `INC0012345` are looked up directly. A caret in the search box cannot add extra query clauses.

Closing a request can be rejected by ServiceNow when request items are still open. Close the items first, or read the error in the banner. Picking Resolved on an incident opens the close-code panel instead of saving a resolved state with no notes.

## Run it

This is a Windows WPF app for 64-bit Windows 10 or 11. It needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), which includes the desktop runtime.

From a Windows machine, in this folder:

```powershell
dotnet run --project src/ServiceNowDesk.App
```

To build a folder the team can copy and run without installing the runtime:

```powershell
.\build-windows.ps1
```

That writes `dist\ServiceNowDesk\ServiceNowDesk.exe`.

## Develop

```bash
dotnet test ServiceNowDesk.sln -c Release
```

The ServiceNow client, query builder, and ticket workspaces are covered by unit tests, including a practice-data round trip for create, update, resolve, search, and catalog order. The WPF project targets `net10.0-windows`.
