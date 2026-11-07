# Set up Google sign-in and Gmail

The app runs without these in Development, using the test sign-in and writing
emails to the log. Follow this guide to use real Google sign-in and real email.
Run the commands from the folder containing `SimpleTasks.sln`.

The values below are secrets. Keep them in user-secrets or `.env`, and never
put them in a committed file.

## 1. Google sign-in

1. Open [Google Cloud Console](https://console.cloud.google.com/) and create a project.
2. Go to **APIs & Services > OAuth consent screen**. Choose **External**, and
   fill in the app name and your email.
3. While the app is in **Testing** status, only the accounts listed under
   **Test users** can sign in. Add every person you plan to invite.
4. Go to **Credentials > Create credentials > OAuth client ID** and choose
   **Web application**.
5. Under **Authorized redirect URIs**, add exactly:
   `http://localhost:5080/signin-google`
6. Copy the client ID and client secret, then run:

```bash
dotnet user-secrets set "Authentication:Google:ClientId" "YOUR_CLIENT_ID" --project SimpleTasks
```

```bash
dotnet user-secrets set "Authentication:Google:ClientSecret" "YOUR_CLIENT_SECRET" --project SimpleTasks
```

Restart the app. The sign-in page now shows **Continue with Google**.

## 2. Gmail

Use a Gmail account that sends the app's email. It needs 2-Step Verification.

1. Turn on 2-Step Verification in your [Google Account security settings](https://myaccount.google.com/security).
2. Create an [App Password](https://myaccount.google.com/apppasswords) and copy it.
3. Run:

```bash
dotnet user-secrets set "Gmail:Address" "you@gmail.com" --project services/SimpleTasks.Notifications
```

```bash
dotnet user-secrets set "Gmail:AppPassword" "YOUR_APP_PASSWORD" --project services/SimpleTasks.Notifications
```

Restart the app. Emails now go out through Gmail instead of the log. A personal
Gmail account can send about 500 emails a day.

## Docker Compose

Compose doesn't read user-secrets. Put the same values in your `.env` file, using
the names in `.env.example`.
