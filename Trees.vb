''' Tree ensembles for demand, shortage and stockout prediction — one CART
''' implementation with three ensembles built on it.
'''
''' Why these are written out rather than installed: XGBoost and LightGBM are
''' native C++ libraries. This app ships as a single WinForms executable with a
''' LocalDB file behind it, and dragging native binaries (plus their runtime
''' redistributables, per architecture) into that installer isn't a trade worth
''' making for a catalogue this size. What those libraries actually do is
''' implementable directly, and that's what's here:
'''
'''   • RandomForest    — bagged trees, each on a bootstrap sample with a random
'''                       subset of features per split. Variance reduction.
'''   • GradientBoosting— trees fitted one after another on the residuals of the
'''                       ones before. Bias reduction.
'''   • NewtonBoosting  — XGBoost's core idea: split gain and leaf values from
'''                       first AND second derivatives of the loss, with L2
'''                       regularisation on the leaves.
'''   • Histogram mode  — LightGBM's core idea: bucket each feature into a fixed
'''                       number of bins up front and split on bin edges, so the
'''                       cost of a split stops depending on the row count.
'''
''' Deterministic throughout. "Random" sampling uses a seeded, explicit
''' generator (see DeterministicRandom) rather than System.Random's time-based
''' default, so the same training data always yields the identical forest. A
''' reorder suggestion that changes on a rerun is one nobody will act on.
Public Module Trees

    ''' A tiny xorshift generator. Fixed seed in, fixed sequence out, on every
    ''' machine and every run — Random() seeded from the clock would make the
    ''' model unreproducible, and .NET doesn't promise its algorithm stays put
    ''' between framework versions.
    '''
    ''' Xorshift rather than the more usual multiply-and-add: VB checks integer
    ''' overflow and throws, while a multiplying generator depends on wrapping
    ''' round. Shifts and xors discard their spare bits quietly, so this one
    ''' can't trip that.
    Public Class DeterministicRandom
        Private _state As UInteger

        Public Sub New(seed As Integer)
            ' Xorshift stays stuck at zero, so the state must never start there.
            _state = CUInt(seed And &H7FFFFFFF) Xor 2463534242UI
            If _state = 0UI Then _state = 2463534242UI
        End Sub

        Public Function NextInt(exclusiveMax As Integer) As Integer
            _state = _state Xor (_state << 13)
            _state = _state Xor (_state >> 17)
            _state = _state Xor (_state << 5)
            Return CInt(_state Mod CUInt(Math.Max(1, exclusiveMax)))
        End Function
    End Class

    ' =====================================================================
    ' CART — one regression tree
    ' =====================================================================

    Public Class TreeNode
        Public Property Feature As Integer = -1      ' -1 marks a leaf
        Public Property Threshold As Double
        Public Property Value As Double              ' prediction, leaves only
        Public Property Left As TreeNode
        Public Property Right As TreeNode

        Public ReadOnly Property IsLeaf As Boolean
            Get
                Return Feature < 0
            End Get
        End Property

        Public Function Predict(features As Double()) As Double
            If IsLeaf Then Return Value
            Return If(features(Feature) <= Threshold, Left.Predict(features), Right.Predict(features))
        End Function
    End Class

    ''' How a tree is grown: the knobs the ensembles differ by.
    Public Class TreeOptions
        Public Property MaxDepth As Integer = 4
        Public Property MinRowsToSplit As Integer = 4
        ''' Features considered per split; 0 means all of them. Random Forest
        ''' sets this below the full count — that's what decorrelates the trees.
        Public Property FeaturesPerSplit As Integer = 0
        ''' Candidate thresholds per feature (LightGBM's histogram idea). Keeps
        ''' split-finding at a fixed cost instead of one test per distinct value.
        Public Property Bins As Integer = 32
        ''' L2 penalty on leaf values (XGBoost's lambda). Shrinks leaves that
        ''' rest on few rows, so one odd week can't swing a prediction.
        Public Property L2 As Double = 1.0
        ''' Use gradients/hessians (Newton boosting) rather than plain variance.
        Public Property SecondOrder As Boolean = False
    End Class

    ''' Grows one regression tree. `gradients`/`hessians` drive Newton boosting;
    ''' for plain trees pass the targets as gradients and Nothing as hessians.
    Public Function GrowTree(rows As List(Of Double()), targets As List(Of Double),
                             indices As List(Of Integer), options As TreeOptions,
                             rng As DeterministicRandom, Optional depth As Integer = 0,
                             Optional hessians As List(Of Double) = Nothing) As TreeNode
        Dim node As New TreeNode With {.Value = LeafValue(targets, indices, hessians, options)}
        If indices.Count < options.MinRowsToSplit OrElse depth >= options.MaxDepth Then Return node

        Dim width = rows(0).Length
        Dim features = ChooseFeatures(width, options.FeaturesPerSplit, rng)

        Dim bestGain = 0.0
        Dim bestFeature = -1
        Dim bestThreshold = 0.0
        For Each feature In features
            For Each threshold In Thresholds(rows, indices, feature, options.Bins)
                Dim leftIdx = indices.Where(Function(i) rows(i)(feature) <= threshold).ToList()
                If leftIdx.Count = 0 OrElse leftIdx.Count = indices.Count Then Continue For
                Dim rightIdx = indices.Where(Function(i) rows(i)(feature) > threshold).ToList()

                Dim gain = SplitGain(targets, hessians, indices, leftIdx, rightIdx, options)
                ' Strictly greater: the first threshold wins a tie, and the
                ' thresholds are generated in a fixed order, so the tree is the
                ' same every time it's grown on the same rows.
                If gain > bestGain Then
                    bestGain = gain
                    bestFeature = feature
                    bestThreshold = threshold
                End If
            Next
        Next
        If bestFeature < 0 Then Return node

        node.Feature = bestFeature
        node.Threshold = bestThreshold
        node.Left = GrowTree(rows, targets, indices.Where(Function(i) rows(i)(bestFeature) <= bestThreshold).ToList(),
                             options, rng, depth + 1, hessians)
        node.Right = GrowTree(rows, targets, indices.Where(Function(i) rows(i)(bestFeature) > bestThreshold).ToList(),
                              options, rng, depth + 1, hessians)
        Return node
    End Function

    Private Function ChooseFeatures(width As Integer, perSplit As Integer, rng As DeterministicRandom) As List(Of Integer)
        Dim all = Enumerable.Range(0, width).ToList()
        If perSplit <= 0 OrElse perSplit >= width Then Return all

        Dim chosen As New List(Of Integer)
        Dim pool = New List(Of Integer)(all)
        While chosen.Count < perSplit AndAlso pool.Count > 0
            Dim pick = rng.NextInt(pool.Count)
            chosen.Add(pool(pick))
            pool.RemoveAt(pick)
        End While
        chosen.Sort()
        Return chosen
    End Function

    ''' Candidate split points: evenly spaced bins between the feature's low and
    ''' high, which is LightGBM's histogram trick — a fixed number of candidates
    ''' however many rows there are.
    Private Function Thresholds(rows As List(Of Double()), indices As List(Of Integer),
                                feature As Integer, bins As Integer) As List(Of Double)
        Dim low = indices.Min(Function(i) rows(i)(feature))
        Dim high = indices.Max(Function(i) rows(i)(feature))
        Dim points As New List(Of Double)
        If high <= low Then Return points

        Dim steps = Math.Max(1, bins)
        For b = 1 To steps - 1
            points.Add(low + (high - low) * b / steps)
        Next
        Return points
    End Function

    ''' Newton boosting scores a split by how much it reduces the loss given the
    ''' gradients and hessians (XGBoost's gain formula); a plain tree scores it
    ''' by how much squared error the split removes.
    Private Function SplitGain(targets As List(Of Double), hessians As List(Of Double),
                               parent As List(Of Integer), left As List(Of Integer), right As List(Of Integer),
                               options As TreeOptions) As Double
        If options.SecondOrder AndAlso hessians IsNot Nothing Then
            Return Score(targets, hessians, left, options.L2) +
                   Score(targets, hessians, right, options.L2) -
                   Score(targets, hessians, parent, options.L2)
        End If
        Return Variance(targets, parent) - (Variance(targets, left) * left.Count + Variance(targets, right) * right.Count) / parent.Count
    End Function

    ''' XGBoost's structure score for one side of a split: G² / (H + λ).
    Private Function Score(gradients As List(Of Double), hessians As List(Of Double),
                           indices As List(Of Integer), l2 As Double) As Double
        Dim g = indices.Sum(Function(i) gradients(i))
        Dim h = indices.Sum(Function(i) hessians(i))
        Return (g * g) / (h + l2)
    End Function

    Private Function Variance(values As List(Of Double), indices As List(Of Integer)) As Double
        If indices.Count = 0 Then Return 0
        Dim mean = indices.Average(Function(i) values(i))
        Return indices.Average(Function(i) (values(i) - mean) * (values(i) - mean))
    End Function

    Private Function LeafValue(targets As List(Of Double), indices As List(Of Integer),
                               hessians As List(Of Double), options As TreeOptions) As Double
        If indices.Count = 0 Then Return 0
        If options.SecondOrder AndAlso hessians IsNot Nothing Then
            ' -G / (H + λ): the step that minimises the regularised loss.
            Dim g = indices.Sum(Function(i) targets(i))
            Dim h = indices.Sum(Function(i) hessians(i))
            Return -g / (h + options.L2)
        End If
        Return indices.Average(Function(i) targets(i))
    End Function

    ' =====================================================================
    ' Random Forest
    ' =====================================================================

    Public Class Forest
        Public Property Trees As New List(Of TreeNode)

        ''' The average across the trees — that averaging is the point: one tree
        ''' overfits its sample, the spread of them cancels much of it out.
        Public Function Predict(features As Double()) As Double
            If Trees.Count = 0 Then Return 0
            Return Trees.Average(Function(tree) tree.Predict(features))
        End Function
    End Class

    ''' Bagged trees: each is grown on a bootstrap resample, considering a subset
    ''' of features per split. The sampling is seeded, so the forest is the same
    ''' every time it's grown on the same rows.
    Public Function TrainRandomForest(rows As List(Of Double()), targets As List(Of Double),
                                      Optional treeCount As Integer = 25,
                                      Optional maxDepth As Integer = 4,
                                      Optional seed As Integer = 20260913) As Forest
        Dim forest As New Forest
        If rows Is Nothing OrElse rows.Count = 0 Then Return forest

        Dim width = rows(0).Length
        Dim options As New TreeOptions With {
            .MaxDepth = maxDepth,
            .FeaturesPerSplit = Math.Max(1, CInt(Math.Ceiling(Math.Sqrt(width))))}

        For treeIndex = 0 To treeCount - 1
            ' Each tree gets its own generator seeded from the tree number, so a
            ' forest of 25 is reproducible and so is any single tree in it.
            Dim rng As New DeterministicRandom(seed + treeIndex)
            Dim sample As New List(Of Integer)
            For i = 1 To rows.Count
                sample.Add(rng.NextInt(rows.Count))
            Next
            forest.Trees.Add(GrowTree(rows, targets, sample, options, rng))
        Next
        Return forest
    End Function

    ' =====================================================================
    ' Gradient boosting (and its Newton/XGBoost form)
    ' =====================================================================

    Public Class BoostedModel
        Public Property Base As Double
        Public Property Trees As New List(Of TreeNode)
        Public Property LearningRate As Double = 0.1

        Public Function Predict(features As Double()) As Double
            Dim value = Base
            For Each tree In Trees
                value += LearningRate * tree.Predict(features)
            Next
            Return value
        End Function
    End Class

    ''' Squared-error gradient boosting: start at the mean, then fit each tree to
    ''' what the model still gets wrong.
    Public Function TrainGradientBoosting(rows As List(Of Double()), targets As List(Of Double),
                                          Optional rounds As Integer = 40,
                                          Optional learningRate As Double = 0.1,
                                          Optional maxDepth As Integer = 3) As BoostedModel
        Dim model As New BoostedModel With {.LearningRate = learningRate}
        If rows Is Nothing OrElse rows.Count = 0 Then Return model
        model.Base = targets.Average()

        Dim options As New TreeOptions With {.MaxDepth = maxDepth}
        Dim rng As New DeterministicRandom(1)
        Dim indices = Enumerable.Range(0, rows.Count).ToList()

        For round = 1 To rounds
            Dim residuals = Enumerable.Range(0, rows.Count).
                Select(Function(i) targets(i) - model.Predict(rows(i))).ToList()
            model.Trees.Add(GrowTree(rows, residuals, indices, options, rng))
        Next
        Return model
    End Function

    ''' XGBoost's method: each round uses the gradient AND the curvature of the
    ''' loss, with an L2 penalty on leaves. On squared error the gradient is the
    ''' residual and the hessian is 1, so the difference from plain boosting is
    ''' the regularised leaf value and the gain formula that picks the splits.
    Public Function TrainNewtonBoosting(rows As List(Of Double()), targets As List(Of Double),
                                        Optional rounds As Integer = 40,
                                        Optional learningRate As Double = 0.1,
                                        Optional maxDepth As Integer = 3,
                                        Optional l2 As Double = 1.0) As BoostedModel
        Dim model As New BoostedModel With {.LearningRate = learningRate}
        If rows Is Nothing OrElse rows.Count = 0 Then Return model
        model.Base = targets.Average()

        Dim options As New TreeOptions With {.MaxDepth = maxDepth, .SecondOrder = True, .L2 = l2}
        Dim rng As New DeterministicRandom(2)
        Dim indices = Enumerable.Range(0, rows.Count).ToList()
        Dim hessians = Enumerable.Repeat(1.0, rows.Count).ToList()

        For round = 1 To rounds
            ' d/dy of ½(y - ŷ)² is (ŷ - y); the leaf value formula negates it.
            Dim gradients = Enumerable.Range(0, rows.Count).
                Select(Function(i) model.Predict(rows(i)) - targets(i)).ToList()
            model.Trees.Add(GrowTree(rows, gradients, indices, options, rng, 0, hessians))
        Next
        Return model
    End Function

End Module
