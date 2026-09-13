Imports System.IO
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' First-run database setup: creating the schema, doing nothing on later runs,
''' and re-attaching data files the engine has forgotten (what happens when
''' LocalDB is reinstalled or the app is reinstalled).
<TestClass>
Public Class DbBootstrapTests

    <TestMethod>
    Public Sub Creates_full_schema_and_seed_rows()
        TestDb.Rebuild()
        Assert.IsTrue(TestDb.Count("SELECT COUNT(*) FROM sys.tables") >= 20, "expected the full set of tables")
        Assert.IsTrue(TestDb.Count("SELECT COUNT(*) FROM sys.views") >= 5, "expected the reporting views")
        Assert.IsTrue(TestDb.Count("SELECT COUNT(*) FROM Users") > 0, "expected seeded user accounts")
        Assert.IsTrue(TestDb.Count("SELECT COUNT(*) FROM Warehouses WHERE Name IN ('Lawal warehouse','Shore warehouse')") = 2)
    End Sub

    <TestMethod>
    Public Sub Running_again_changes_nothing()
        Dim products = TestDb.Count("SELECT COUNT(*) FROM Products")
        Assert.AreEqual("", DbBootstrap.EnsureDatabase())
        Assert.AreEqual("", DbBootstrap.EnsureDatabase())
        Assert.AreEqual(products, TestDb.Count("SELECT COUNT(*) FROM Products"), "second run must not re-seed")
    End Sub

    <TestMethod>
    Public Sub Database_files_live_in_the_configured_folder()
        Assert.AreEqual("", DbBootstrap.EnsureDatabase())
        Dim mdf = Path.Combine(DbBootstrap.DataDirectory(), "StockDeskDB.mdf")
        Assert.IsTrue(File.Exists(mdf), "expected the data file at " & mdf)
    End Sub

    <TestMethod>
    Public Sub Re_attaches_existing_data_after_the_engine_forgets_it()
        TestDb.Rebuild()
        Dim marker = "Reattach " & Guid.NewGuid().ToString("N").Substring(0, 6)
        TestDb.AddProduct(marker)

        ' Detach: files stay on disk, the instance no longer knows the database.
        Dim csb As New Data.SqlClient.SqlConnectionStringBuilder(TestDb.ConnectionString()) With {.InitialCatalog = "master"}
        Using conn As New Data.SqlClient.SqlConnection(csb.ConnectionString)
            conn.Open()
            Using cmd As New Data.SqlClient.SqlCommand(
                "ALTER DATABASE StockDeskDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ALTER DATABASE StockDeskDB SET MULTI_USER; EXEC sp_detach_db 'StockDeskDB'", conn)
                cmd.ExecuteNonQuery()
            End Using
        End Using

        Assert.AreEqual("", DbBootstrap.EnsureDatabase(), "re-attach should succeed, not fail on existing files")
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Products WHERE Name = @n", TestDb.P("@n", marker)),
                        "the customer's data must survive a re-attach")
    End Sub

    <TestMethod>
    Public Sub Backup_writes_a_restorable_file()
        Assert.AreEqual("", DbBootstrap.EnsureDatabase())
        Dim bakPath = Path.Combine(Path.GetTempPath(), "chewystock_test_" & Guid.NewGuid().ToString("N").Substring(0, 6) & ".bak")
        Try
            Assert.AreEqual("", DbBootstrap.BackupTo(bakPath))
            Assert.IsTrue(New FileInfo(bakPath).Length > 100000, "a real backup should not be tiny")
        Finally
            If File.Exists(bakPath) Then File.Delete(bakPath)
        End Try
    End Sub

End Class
