Imports System.Data

''' Market basket analysis: which products go out of the door together.
'''
''' Two miners are implemented over the same data because they answer the same
''' question by different routes — Apriori (breadth-first, generate-and-test) and
''' FP-Growth (depth-first over a prefix tree). They must always agree; that
''' agreement is what the tests check, and it's the cheapest guard there is
''' against a subtle counting bug in either one.
'''
''' Everything here is deterministic: items are ordered by count then by ID, and
''' itemsets are keyed by their sorted members, so the same database always
''' produces the same rules in the same order. No sampling, no random seeds.
'''
''' What it's for: "customers who buy Adult Dog Food 20kg also take Puppy Chow"
''' is a shelf-placement, bundle and reorder decision. Lift is the number that
''' matters — confidence alone just re-discovers your best seller.
Public Module MarketBasket

    ''' One discovered rule: buying everything in Antecedent goes with Consequent.
    Public Class Rule
        Public Property Antecedent As String()
        Public Property Consequent As String
        ''' Share of all baskets containing every item in the rule.
        Public Property Support As Double
        ''' Of the baskets with the antecedent, the share that also had the consequent.
        Public Property Confidence As Double
        ''' Confidence against the consequent's own popularity. >1 means a real
        ''' association; ~1 means the two just both happen to be common.
        Public Property Lift As Double
        ''' How many baskets back the rule up — a rule from three baskets is a
        ''' coincidence, and the count is what lets a reader judge that.
        Public Property Baskets As Integer

        Public ReadOnly Property Explanation As String
            Get
                Return $"{String.Join(" + ", Antecedent)} → {Consequent}: " &
                       $"in {Baskets} basket(s), {Confidence:P0} of the time, {Lift:0.0}× more often than chance."
            End Get
        End Property
    End Class

    ''' Every sale as a basket of product IDs, newest window first. One basket
    ''' per invoice; quantities don't matter here, only co-occurrence.
    Public Function BasketsFromSales(Optional daysBack As Integer = 365) As List(Of List(Of Integer))
        Dim rows = DataAccess.GetTable(
            "SELECT ii.InvoiceID, ii.ProductID FROM InvoiceItems ii " &
            "JOIN Invoices i ON i.InvoiceID = ii.InvoiceID " &
            "WHERE i.InvoiceDate >= DATEADD(DAY, @d, CAST(GETDATE() AS DATE)) " &
            "GROUP BY ii.InvoiceID, ii.ProductID ORDER BY ii.InvoiceID, ii.ProductID",
            New Dictionary(Of String, Object) From {{"@d", -Math.Abs(daysBack)}})

        Dim baskets As New List(Of List(Of Integer))
        Dim current As List(Of Integer) = Nothing
        Dim currentId As Integer = -1
        For Each r As DataRow In rows.Rows
            Dim invoiceId = Convert.ToInt32(r("InvoiceID"))
            If invoiceId <> currentId Then
                current = New List(Of Integer)
                baskets.Add(current)
                currentId = invoiceId
            End If
            current.Add(Convert.ToInt32(r("ProductID")))
        Next
        Return baskets
    End Function

    Public Function ProductNames() As Dictionary(Of Integer, String)
        Dim names As New Dictionary(Of Integer, String)
        For Each r As DataRow In DataAccess.GetTable("SELECT ProductID, Name FROM Products").Rows
            names(Convert.ToInt32(r("ProductID"))) = Convert.ToString(r("Name"))
        Next
        Return names
    End Function

    ' ===== itemset keys =====

    ''' Itemsets are keyed by their sorted members, so {2,1} and {1,2} are the
    ''' same set however they were built.
    Private Function Key(items As IEnumerable(Of Integer)) As String
        Return String.Join(",", items.OrderBy(Function(i) i))
    End Function

    Private Function Items(key As String) As Integer()
        Return key.Split(","c).Select(AddressOf Integer.Parse).ToArray()
    End Function

    ' ===== Apriori =====

    ''' Frequent itemsets by Apriori: count singles, keep those that clear the
    ''' threshold, join survivors into pairs, count again, and so on. The prune
    ''' step is what makes it affordable — an itemset can't be frequent if any
    ''' subset of it isn't, so it never gets counted.
    ''' Returns itemset key → number of baskets containing it.
    Public Function Apriori(baskets As List(Of List(Of Integer)), minSupport As Double,
                            Optional maxItemsetSize As Integer = 3) As Dictionary(Of String, Integer)
        Dim found As New Dictionary(Of String, Integer)
        If baskets.Count = 0 Then Return found
        Dim minCount = MinimumCount(baskets.Count, minSupport)

        Dim sets = baskets.Select(Function(b) New HashSet(Of Integer)(b)).ToList()

        ' Level 1: the items themselves.
        Dim counts As New Dictionary(Of Integer, Integer)
        For Each basket In sets
            For Each item In basket
                counts(item) = If(counts.ContainsKey(item), counts(item), 0) + 1
            Next
        Next
        Dim current = counts.Where(Function(kv) kv.Value >= minCount).
                             OrderBy(Function(kv) kv.Key).
                             Select(Function(kv) New Integer() {kv.Key}).ToList()
        For Each itemset In current
            found(Key(itemset)) = counts(itemset(0))
        Next

        Dim size = 1
        While current.Count > 0 AndAlso size < maxItemsetSize
            Dim candidates = Join(current)
            Dim levelCounts As New Dictionary(Of String, Integer)
            For Each candidate In candidates
                Dim hits = sets.Where(Function(b) candidate.All(Function(i) b.Contains(i))).Count()
                If hits >= minCount Then levelCounts(Key(candidate)) = hits
            Next
            For Each kv In levelCounts
                found(kv.Key) = kv.Value
            Next
            current = levelCounts.Keys.OrderBy(Function(k) k).Select(AddressOf Items).ToList()
            size += 1
        End While
        Return found
    End Function

    ''' Candidates of size k+1 from frequent sets of size k: join two that share
    ''' their first k-1 items, then drop any candidate with an infrequent subset.
    Private Function Join(frequent As List(Of Integer())) As List(Of Integer())
        Dim known As New HashSet(Of String)(frequent.Select(AddressOf Key))
        Dim candidates As New List(Of Integer())
        Dim seen As New HashSet(Of String)
        For i = 0 To frequent.Count - 1
            For j = i + 1 To frequent.Count - 1
                Dim a = frequent(i), b = frequent(j)
                Dim sharesPrefix = True
                For k = 0 To a.Length - 2
                    If a(k) <> b(k) Then sharesPrefix = False : Exit For
                Next
                If Not sharesPrefix OrElse a.Last() >= b.Last() Then Continue For

                Dim candidate = a.Concat({b.Last()}).OrderBy(Function(x) x).ToArray()
                Dim candidateKey = Key(candidate)
                If Not seen.Add(candidateKey) Then Continue For
                ' Every subset one smaller must itself be frequent.
                Dim viable = candidate.All(Function(drop) known.Contains(Key(candidate.Where(Function(x) x <> drop))))
                If viable Then candidates.Add(candidate)
            Next
        Next
        Return candidates
    End Function

    ' ===== FP-Growth =====

    Private Class FpNode
        Public Property Item As Integer = -1
        Public Property Count As Integer
        Public Property Parent As FpNode
        Public ReadOnly Children As New SortedDictionary(Of Integer, FpNode)
    End Class

    ''' Frequent itemsets by FP-Growth: pack the baskets into a prefix tree, then
    ''' mine it depth-first from the rarest item up, so each step works on a
    ''' smaller conditional tree instead of rescanning every basket.
    ''' Same answer as Apriori — the tests hold the two against each other.
    Public Function FpGrowth(baskets As List(Of List(Of Integer)), minSupport As Double,
                             Optional maxItemsetSize As Integer = 3) As Dictionary(Of String, Integer)
        Dim found As New Dictionary(Of String, Integer)
        If baskets.Count = 0 Then Return found
        Dim minCount = MinimumCount(baskets.Count, minSupport)

        Dim transactions = baskets.Select(Function(b) b.Distinct().ToList()).ToList()
        Mine(transactions, New Integer() {}, minCount, maxItemsetSize, found)
        Return found
    End Function

    ''' One round: count what's left, build the tree, and recurse into each
    ''' frequent item's conditional pattern base.
    Private Sub Mine(transactions As List(Of List(Of Integer)), suffix As Integer(),
                     minCount As Integer, maxItemsetSize As Integer, found As Dictionary(Of String, Integer))
        If suffix.Length >= maxItemsetSize Then Return

        Dim counts As New Dictionary(Of Integer, Integer)
        For Each basket In transactions
            For Each item In basket
                counts(item) = If(counts.ContainsKey(item), counts(item), 0) + 1
            Next
        Next
        ' Rarest first so the recursion shrinks fastest; ID breaks ties so the
        ' ordering — and therefore the output — never depends on hash order.
        Dim frequent = counts.Where(Function(kv) kv.Value >= minCount).
                              OrderBy(Function(kv) kv.Value).ThenBy(Function(kv) kv.Key).
                              Select(Function(kv) kv.Key).ToList()

        For Each item In frequent
            Dim itemset = suffix.Concat({item}).OrderBy(Function(x) x).ToArray()
            found(Key(itemset)) = counts(item)

            ' Conditional base: the baskets holding this item, minus the item and
            ' anything already dealt with at this level.
            Dim earlier = New HashSet(Of Integer)(frequent.TakeWhile(Function(f) f <> item))
            Dim conditional = transactions.
                Where(Function(basket) basket.Contains(item)).
                Select(Function(basket) basket.Where(Function(x) x <> item AndAlso Not earlier.Contains(x)).ToList()).
                Where(Function(basket) basket.Count > 0).ToList()
            If conditional.Count > 0 Then Mine(conditional, itemset, minCount, maxItemsetSize, found)
        Next
    End Sub

    ''' At least two baskets, always — one basket is an anecdote, not a pattern.
    Private Function MinimumCount(basketCount As Integer, minSupport As Double) As Integer
        Return Math.Max(2, CInt(Math.Ceiling(minSupport * basketCount)))
    End Function

    ' ===== rules =====

    ''' Turns frequent itemsets into "buy A, expect B" rules, strongest lift
    ''' first. Consequents are single products: a rule a person can act on.
    Public Function RulesFrom(frequent As Dictionary(Of String, Integer), basketCount As Integer,
                              names As Dictionary(Of Integer, String),
                              Optional minConfidence As Double = 0.3) As List(Of Rule)
        Dim rules As New List(Of Rule)
        If basketCount = 0 Then Return rules

        For Each entry In frequent.OrderBy(Function(kv) kv.Key)
            Dim itemset = Items(entry.Key)
            If itemset.Length < 2 Then Continue For

            For Each consequent In itemset
                Dim antecedent = itemset.Where(Function(i) i <> consequent).ToArray()
                Dim antecedentKey = Key(antecedent)
                Dim consequentKey = Key({consequent})
                If Not frequent.ContainsKey(antecedentKey) OrElse Not frequent.ContainsKey(consequentKey) Then Continue For

                Dim confidence = entry.Value / CDbl(frequent(antecedentKey))
                If confidence < minConfidence Then Continue For
                Dim consequentSupport = frequent(consequentKey) / CDbl(basketCount)

                rules.Add(New Rule With {
                    .Antecedent = antecedent.Select(Function(i) NameOf_(names, i)).ToArray(),
                    .Consequent = NameOf_(names, consequent),
                    .Support = entry.Value / CDbl(basketCount),
                    .Confidence = confidence,
                    .Lift = If(consequentSupport > 0, confidence / consequentSupport, 0),
                    .Baskets = entry.Value})
            Next
        Next

        Return rules.OrderByDescending(Function(r) r.Lift).
                     ThenByDescending(Function(r) r.Confidence).
                     ThenBy(Function(r) r.Consequent).ToList()
    End Function

    Private Function NameOf_(names As Dictionary(Of Integer, String), id As Integer) As String
        Return If(names IsNot Nothing AndAlso names.ContainsKey(id), names(id), "Product " & id)
    End Function

    ''' The whole job end to end, for the screen: read the sales, mine them, and
    ''' hand back the rules worth showing.
    Public Function DiscoverRules(Optional daysBack As Integer = 365, Optional minSupport As Double = 0.02,
                                  Optional minConfidence As Double = 0.3) As List(Of Rule)
        Dim baskets = BasketsFromSales(daysBack)
        If baskets.Count = 0 Then Return New List(Of Rule)
        Return RulesFrom(FpGrowth(baskets, minSupport), baskets.Count, ProductNames(), minConfidence)
    End Function

End Module
