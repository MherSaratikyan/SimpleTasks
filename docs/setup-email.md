# Set up email

Simple Tasks sends email through Gmail for invites, password resets,
notifications and reminders. People log in with their email and a password;
email is only how those links and messages reach them.

In Development, with no Gmail set up, emails are written to the log instead of
sent, including the links in them. Look for "Email (not sent in Development)" in
the Notifications logs in the Aspire dashboard at http://localhost:15080.

Run the commands from the folder containing `SimpleTasks.sln`. The values are
secrets. Keep them in user-secrets or `.env`, and never in a committed file.

## Connect a Gmail account

Use the Gmail account that will send the app's email. It needs 2-Step Verification.

1. Turn on 2-Step Verification in your [Google Account security settings](https://myaccount.google.com/security).
2. Create an [App Password](https://myaccount.google.com/apppasswords) and copy it.
3. Run:

```bash
dotnet user-secrets set "Gmail:Address" "you@gmail.com" --project services/SimpleTasks.Notifications
```

```bash
dotnet user-secrets set "Gmail:AppPassword" "YOUR_APP_PASSWORD" --project services/SimpleTasks.Notifications
```

Restart the app. Emails now go out through Gmail. A personal Gmail account can
send about 500 emails a day.

## Your first login

The first admin is the email in `FirstAdminEmail` (see the README). Until that
account has a password, each start of the app emails it a link to set one, valid
for 24 hours. Before email is set up, copy the same link from the Identity logs:
look for "The first admin ... has no password yet".

Everyone else joins by invite. Their invite email has a link, valid for 7 days,
where they choose their name and password.

## Docker Compose

Compose doesn't read user-secrets. Put `GMAIL_ADDRESS` and `GMAIL_APP_PASSWORD`
in your `.env` file, as shown in `.env.example`. Compose runs in Production mode,
so without Gmail, your first admin link is only in the Identity logs:

```bash
docker compose logs identity
```
