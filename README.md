# ServiceNow Desk

A Windows desktop app for client services teams who need to create, update, resolve, and search ServiceNow incidents and requests without waiting on the web UI. The menus stay on screen. The app sends the Table API and Service Catalog API calls itself.

## What you can do

- Incidents: create, update, assign, add work notes or customer comments, and resolve with a close code and close notes. **Create new** is on the incident list. **Copy** sits next to the incident, request, request item, and walk-up number. Assignment group and assigned to are dropdowns; the people list is the members of the selected group. Service offering and configuration item are dropdowns on incidents and request items. The closed list shows the name you picked. Service offering is required when you save an incident that already exists, and optional on the first save. Configuration item stays optional. Walk-up interactions do not have these fields. **Templates** on the incident list save the current incident on this PC. One click starts a new incident with those fields filled in. Nothing is sent until you click Save.
- Walk-up interactions (`interaction`, number prefix IMS): log a person who walked up, then turn that interaction into an incident. The list defaults to the same filters as incidents (my open, my groups, unassigned, all open, closed). New records are saved with type `walkup`. **Create incident** on a saved interaction copies the opened-for person, short description, description, assignment group, and assigned to onto a new incident, links the two records, and opens the incident so you can finish it. Save the walk-up first. A second click opens the incident already linked to that IMS record instead of creating another one.
- Requests (`sc_request`): create a direct request, update it, and close it.
- Request items (`sc_req_item`): update, assign, and close the items agents actually fulfill. Open them from the request they belong to.
- Catalog orders: search the service catalog, fill variables, and order an item for a caller. This is the path that runs the normal catalog workflow.
- Search: one box searches the current list. The Search page looks across incidents, requests, items, and knowledge at once, including closed records. Open a hit to read it, then use Back to return to the results.
- Knowledge: articles opened from Search.

Practice data is built in, so the team can learn the layout before an instance is connected. Nothing in practice mode is sent to ServiceNow.

## Keyboard

| Shortcut | Action |
| --- | --- |
| Ctrl+1 … Ctrl+6 | Incidents, Request items, Request items, Search, Order catalog, Connection |
| Ctrl+7 | Knowledge |
| Ctrl+8 | Walk-up |
| Ctrl+9 | Notifications |
| Ctrl+K or Ctrl+F | Focus search |
| Ctrl+N | New incident or request |
| Ctrl+S | Save |
| Ctrl+Enter | Post the note, or confirm resolve when that panel is open |
| Ctrl+Shift+R | Resolve / close |
| F5 | Refresh the current list |
| Esc | Close the resolve panel |

Type a person's name in Caller or Requested for. After two characters the desk searches active users by name, email, and user id, and lists the name and email under the field. Enter accepts the highlighted person. If the typed text matches one person, Save uses that person even when you did not click the row. Assignment group and Assigned to are dropdowns. The last five groups you pick, or save on an incident, stay at the top of the group dropdown in alphabetical order. That list is on this PC in `%AppData%\ServiceNowDesk\recent-assignment-groups.json`. It is not a secret, and practice mode uses the same file. Choosing a group lists every member of that group. If ServiceNow returns only one page, the desk follows the rest.

Requests stay out of the left navigation. Request items stay. Ctrl+2 opens Request items. Opening a request item can still show the parent request.

Lists of incidents, request items, walk-ups, and search can tint a row. Settings, then Legend, explains each color and turns it on or off. The choice is saved on this PC with the other settings. The light red row is Unassigned: nobody is assigned. A group with no person is included. A person who is assigned, even with no group, stays on the normal background. The other colors are light tints of the notification circles (SLA breaching, on hold past follow-up, updated by caller, returned with notes, assigned to me, and the group queue). Assigned to me and the group queue start off so a list of your own tickets is not painted end to end. When a ticket matches more than one highlight that is on, the entry higher in the legend is the color you see. Hover and the selected row keep the usual teal.

A saved incident or request item lists its attachment file names. Click a name to download it and open it with the usual Windows app. A new incident has no attachments until you save it. Practice data includes one sample attachment on the printer incident and the laptop request item.

## Connect to your instance

1. Open **Connection**.
2. Enter the instance URL, for example `https://company.service-now.com`.
3. Choose a sign-in method.
   - **Browser sign-in (SSO)** when the company page uses single sign-on. Click **Sign in with browser**, finish the company sign-in, and the window closes once ServiceNow accepts the session. The app then calls the same APIs with that session. This needs the [WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/), which current Windows 10 and 11 installs already include.
   - **Username and password** when the instance accepts it. Sign in as the agent. "My open" and "My groups" then mean that person. If ServiceNow answers that auth information is required, the instance expects single sign-on: use browser sign-in instead.
   - **OAuth password grant** uses an OAuth client plus the agent's user name and password.
   - **OAuth client credentials** uses the integration user configured on the OAuth app. "My open" is that integration user, not the person at the keyboard.
4. Click **Connect**. A saved browser session connects again for 24 hours after that sign-in, or sooner when the token expires first. A refresh does not extend those 24 hours. When the session is past expiry, or ServiceNow rejects it, the app clears the saved session and returns to Connection so you can sign in again.

The password, client secret, and browser session are stored with Windows DPAPI for the current Windows user, under `%AppData%\ServiceNowDesk\settings.json`. They are not written in plain text.

Dropdowns for incidents, requests, request items, and walk-ups, plus catalog questions you have opened, are saved on this PC and refreshed about once a day. Open incidents, requests, request items, and walk-ups are saved the same way. The forms and those lists read the saved copy when it is newer than a day. Saves, notes, and new catalog orders still go to ServiceNow. Choice lists and assignment groups live in `%AppData%\ServiceNowDesk\form-catalog.<instance>.json`. Ticket lists live in `%AppData%\ServiceNowDesk\lists.<instance>.json`.

Connect and practice mode open a download screen only when something on this PC is missing or older than a day. The title reads "Downloading data 2/8", and each section stays on its own line: Choices, Assignment groups, Assignment group members, Service offerings, Configuration items, Incidents, Requests, and Walk-ups. Service offerings are the active rows, paged until complete. Configuration items are the active CMDB rows, paged up to 5,000; the line says when that limit stops the download, and the rows already saved stay on this PC. A failed refresh keeps the previous copy. Practice mode includes a few sample offerings and configuration items. Close the screen while it is still running and a single bar at the top shows how many sections are finished, such as 3/8 and 50%. Closing the screen does not stop the download, and the desk stays usable under that bar. When every section finishes, the screen and the bar both go away. They do not stay up as "Data ready". If every saved copy is newer than a day, neither the screen nor the bar appears, and ServiceNow is not asked for that data again.

Updated by caller lists a record only when the caller made the latest update, it is assigned to you or to the watched group or is unassigned in that group or with no group, and its location is one of the notification offices. If no offices are set, the office check is skipped.

F5 refreshes the list you are looking at. It does not open the download screen. To force a saved copy to download again, open Settings and use Cache. Each row has a refresh button. Refresh all clears every listed cache and downloads them again. That screen does not delete incident templates or the saved sign-in. A failed refresh leaves the other caches in place.

Type a caller's name, user id, or email. The desk searches ServiceNow as you type. If that text matches one person, Save uses that person. If several people match, choose the row from the list.

Incident templates are stored in `%AppData%\ServiceNowDesk\incident-templates.json`. They are local to this PC, including practice mode, and they are not secrets. Saving a template does not create an incident in ServiceNow.

### OAuth app

In ServiceNow: **System OAuth > Application Registry > New > Create an OAuth API endpoint for external clients**. Copy the client ID and client secret. For client credentials, set the OAuth application user on that registry record.

### Access the account needs

The signed-in user needs the same rights they already use in the web UI, typically `itil`, plus permission to:

- read and write `incident`, `sc_request`, `sc_req_item`, and `interaction`
- create `interaction_related_record` rows when converting a walk-up to an incident
- read `sys_user`, `sys_user_group`, `sys_choice`, and `sys_journal_field`
- read attachments on incidents and request items (`/api/now/attachment`)
- order from the service catalog if you use **Order catalog**

If an update is blocked, the red banner shows the message ServiceNow returned.

### How updates are sent

The app uses the Table API:

- `GET/POST/PATCH /api/now/table/incident`
- `GET/POST/PATCH /api/now/table/sc_request`
- `GET/PATCH /api/now/table/sc_req_item`
- `GET/POST/PATCH /api/now/table/interaction` for walk-up IMS records (`type=walkup`)
- `GET/POST /api/now/table/interaction_related_record` to link an interaction to the incident created from it (`interaction`, `document_table=incident`, `document_id`)
- journal notes are `work_notes` or `comments` on that record, including walk-up interactions
- attachments are `GET /api/now/attachment?sysparm_query=table_name={incident|sc_req_item}^table_sys_id={sys_id}` and `GET /api/now/attachment/{sys_id}/file`
- choices, users, and groups come from the matching tables

Catalog ordering uses `POST /api/sn_sc/servicecatalog/items/{sys_id}/order_now`.

Search text is sent with ServiceNow's text index operator (`123TEXTQUERY321`). Ticket numbers such as `INC0012345` are looked up directly. A caret in the search box cannot add extra query clauses.

Closing a request can be rejected by ServiceNow when request items are still open. Close the items first, or read the error in the banner. Picking Resolved on an incident opens the close-code panel instead of saving a resolved state with no notes.

## Walk-up interactions

Walk-up work is an interaction, not an incident. ServiceNow stores it on `interaction` and numbers it IMS. This desk only lists records whose type is `walkup` (the walk-up channel). Opened for is the person at the desk. State and type choices come from `sys_choice` when the instance has them, with the usual walk-up values as a fallback (`new`, `work_in_progress`, `on_hold`, `wrap_up`, `closed_complete`, `closed_abandoned`). Work notes and customer comments use the same journal fields as incidents (`sys_journal_field`).

**Create incident** does three Table API steps:

1. Look up `interaction_related_record` where `interaction` is this record and `document_table` is `incident`. If a row exists, the app opens that incident and does not create another.
2. Otherwise `POST /api/now/table/incident` with the interaction's opened-for person as the caller, plus short description, description, assignment group, and assigned to.
3. `POST /api/now/table/interaction_related_record` with `interaction`, `document_table=incident`, and `document_id` set to the new incident. The new incident then opens in the incident editor.

If the instance rejects `interaction_related_record`, the incident is still created and the red banner shows the ServiceNow error. Practice data includes two sample walk-ups, and **Create incident** there makes a local incident and remembers the link.

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
