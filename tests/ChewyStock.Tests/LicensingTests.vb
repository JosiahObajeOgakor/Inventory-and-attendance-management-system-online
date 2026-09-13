Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Licensing: the machine fingerprint, and the fact that only a properly signed
''' key activates a computer. The app holds the public key only, so it can check
''' keys but never mint one.
<TestClass>
Public Class LicensingTests

    <TestMethod>
    Public Sub Machine_id_is_a_stable_sixteen_character_code()
        Dim id = Licensing.MachineId
        Assert.AreEqual(16, id.Length)
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9A-HJKMNP-TV-Z]+$"), "unexpected characters in " & id)
        Assert.AreEqual(id, Licensing.MachineId, "the same PC must always produce the same id")
    End Sub

    <TestMethod>
    Public Sub Rubbish_keys_are_refused()
        For Each junk In {"", "   ", "not-a-key", "AAAA-BBBB-CCCC", New String("x"c, 86)}
            Assert.IsFalse(Licensing.IsKeyValid(junk), "should have been refused: " & junk)
        Next
    End Sub

    <TestMethod>
    Public Sub A_key_signed_for_another_machine_is_refused()
        ' Shape of a real key (86 base64url chars) but not signed for this PC.
        Dim lookalike = New String("A"c, 86)
        Assert.IsFalse(Licensing.IsKeyValid(lookalike, "0000AAAA1111BBBB"))
        Assert.IsFalse(Licensing.IsKeyValid(lookalike))
    End Sub

    <TestMethod>
    Public Sub Offline_activation_refuses_an_invalid_key_and_leaves_the_app_unactivated()
        Dim before = Licensing.IsActivated()
        Assert.IsFalse(Licensing.ActivateWithOfflineKey("clearly-not-a-signed-key"))
        Assert.AreEqual(before, Licensing.IsActivated(), "a bad key must not change activation state")
    End Sub

    <TestMethod>
    Public Sub Licensing_service_url_points_at_the_live_service()
        Assert.IsTrue(Licensing.ApiBaseUrl.StartsWith("https://"), "activation must go over https")
        Assert.IsFalse(Licensing.ApiBaseUrl.Contains("springupai"), "the old domain is no longer used")
    End Sub

End Class
