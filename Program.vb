Imports System.Windows.Forms

Module Program

    <STAThread>
    Sub Main(args As String())
        ' Support helpers:
        '   StockDesk.exe --machineid            → print this PC's Machine ID
        '   StockDesk.exe --activate <email>     → run the online activation now
        '   StockDesk.exe --setup-db             → create the DB/schema and exit
        '                                          (the installer runs this once, silently)
        If args.Length >= 1 AndAlso args(0) = "--machineid" Then
            Console.WriteLine(Licensing.MachineId)
            Return
        End If
        If args.Length >= 2 AndAlso args(0) = "--activate" Then
            Dim r = Licensing.ActivateOnline(args(1))
            Console.WriteLine($"{r.Status}: {r.Message}")
            Return
        End If
        If args.Length >= 1 AndAlso args(0) = "--setup-db" Then
            Dim setupErr = DbBootstrap.EnsureDatabase()
            If setupErr = "" Then setupErr = DbBootstrap.Migrate()
            Console.WriteLine(If(setupErr = "", "OK", "FAILED: " & setupErr))
            Environment.ExitCode = If(setupErr = "", 0, 1)
            Return
        End If

        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        ' Log and explain anything unexpected instead of vanishing.
        CrashLog.Install()

        ' First run on this PC: create the database + full schema from
        ' Schema.sql. Every run after that returns immediately.
        Dim dbError = DbBootstrap.EnsureDatabase()
        If dbError <> "" Then
            MessageBox.Show(dbError, Theme.AppName & " — database setup failed",
                             MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return
        End If
        ' Adds anything a database from an earlier version is still missing.
        DbBootstrap.Migrate()

        ' Palette/fonts (safe if the DB isn't reachable yet).
        Try
            Theme.Load()
        Catch
        End Try

        ' 1. Activation. The user may Skip — the app still opens so an Admin can
        '    set up their own account and see the buy screen, but every business
        '    module stays locked (frmMain enforces this) until activated.
        If Not Licensing.IsActivated() Then
            Using act As New frmActivation()
                Dim res = act.ShowDialog()
                If res <> DialogResult.OK Then Return          ' Quit
                ' Activated or Skipped → carry on.
            End Using
        End If

        ' 2. There must be at least one Admin with a real password. Allowed even
        '    when not activated — an Admin can create their OWN account on a
        '    fresh install (they just can't add clerks until activated).
        If Not Auth.HasUsableAdmin() Then
            Using reg As New frmRegister(firstAdmin:=True)
                If reg.ShowDialog() <> DialogResult.OK Then Return
            End Using
        End If

        ' 3. Sign in (loop so a failed first-password-set, or a clerk who signs
        '    out instead of checking in, both return here).
        Dim signedIn = False
        Do Until signedIn
            Using login As New frmLogin()
                If login.ShowDialog() <> DialogResult.OK Then Return   ' Quit
                Try
                    Theme.Load()
                Catch
                End Try
                ' First sign-in of the day also writes an automatic backup.
                Threading.Tasks.Task.Run(Sub() BackupService.RunDailyBackup())

                ' Warehouse clerks get the full-screen welcome clip (with a
                ' check-in card); Admin doesn't. She can decline and sign out
                ' instead of checking in — ShowFor then returns False.
                If frmClerkWelcome.ShowFor(login.LoggedInUserID, login.LoggedInRole, login.LoggedInFullName) Then
                    signedIn = True
                    Dim main As New frmMain() With {
                        .CurrentUserID = login.LoggedInUserID,
                        .CurrentUserName = login.LoggedInFullName,
                        .CurrentUserRole = login.LoggedInRole
                    }
                    Application.Run(main)
                End If
            End Using
        Loop
    End Sub

End Module
