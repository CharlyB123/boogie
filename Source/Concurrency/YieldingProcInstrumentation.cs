using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.Contracts;
using Microsoft.Boogie.GraphUtil;

namespace Microsoft.Boogie
{
  class YieldingProcInstrumentation
  {
    public static List<Declaration> TransformImplementations(
      CivlTypeChecker civlTypeChecker,
      LinearTypeChecker linearTypeChecker,
      int layerNum,
      Dictionary<Absy, Absy> absyMap,
      HashSet<Procedure> yieldingProcs,
      Dictionary<CallCmd, Block> refinementBlocks)
    {
      var linearPermissionInstrumentation =
        new LinearPermissionInstrumentation(civlTypeChecker, linearTypeChecker, layerNum, absyMap);
      var yieldingProcInstrumentation = new YieldingProcInstrumentation(
        civlTypeChecker,
        linearTypeChecker,
        linearPermissionInstrumentation,
        layerNum,
        absyMap,
        refinementBlocks);
      yieldingProcInstrumentation.AddNoninterferenceCheckers();
      var implToPreconditions = yieldingProcInstrumentation.CreatePreconditions(linearPermissionInstrumentation);
      yieldingProcInstrumentation
        .InlineYieldRequiresAndEnsures(); // inline after creating the preconditions but before transforming the implementations
      yieldingProcInstrumentation.TransformImpls(yieldingProcs, implToPreconditions);

      List<Declaration> decls = new List<Declaration>();
      decls.AddRange(yieldingProcInstrumentation.noninterferenceCheckerDecls);
      decls.AddRange(yieldingProcInstrumentation.parallelCallAggregators.Values);
      decls.AddRange(yieldingProcInstrumentation.PendingAsyncNoninterferenceCheckers());
      decls.Add(yieldingProcInstrumentation.wrapperNoninterferenceCheckerProc);
      decls.Add(yieldingProcInstrumentation.WrapperNoninterferenceCheckerImpl());
      return decls;
    }

    private CivlTypeChecker civlTypeChecker;
    private LinearTypeChecker linearTypeChecker;
    private int layerNum;
    private Dictionary<Absy, Absy> absyMap;

    private Dictionary<string, Procedure> parallelCallAggregators;
    private List<Declaration> noninterferenceCheckerDecls;
    private Procedure wrapperNoninterferenceCheckerProc;

    private GlobalSnapshotInstrumentation globalSnapshotInstrumentation;
    private RefinementInstrumentation refinementInstrumentation;
    private LinearPermissionInstrumentation linearPermissionInstrumentation;
    private NoninterferenceInstrumentation noninterferenceInstrumentation;

    private Dictionary<CallCmd, Block> refinementBlocks;

    private YieldingProcInstrumentation(
      CivlTypeChecker civlTypeChecker,
      LinearTypeChecker linearTypeChecker,
      LinearPermissionInstrumentation linearPermissionInstrumentation,
      int layerNum,
      Dictionary<Absy, Absy> absyMap,
      Dictionary<CallCmd, Block> refinementBlocks)
    {
      this.civlTypeChecker = civlTypeChecker;
      this.linearTypeChecker = linearTypeChecker;
      this.layerNum = layerNum;
      this.absyMap = absyMap;
      this.linearPermissionInstrumentation = linearPermissionInstrumentation;
      this.refinementBlocks = refinementBlocks;
      parallelCallAggregators = new Dictionary<string, Procedure>();
      noninterferenceCheckerDecls = new List<Declaration>();

      List<Variable> inputs = new List<Variable>();
      foreach (string domainName in linearTypeChecker.linearDomains.Keys)
      {
        inputs.Add(linearTypeChecker.LinearDomainInFormal(domainName));
      }

      foreach (Variable g in civlTypeChecker.GlobalVariables)
      {
        inputs.Add(OldGlobalFormal(g));
      }

      wrapperNoninterferenceCheckerProc = new Procedure(Token.NoToken,
        $"Wrapper_NoninterferenceChecker_{layerNum}", new List<TypeVariable>(),
        inputs, new List<Variable>(), new List<Requires>(), new List<IdentifierExpr>(), new List<Ensures>());
      CivlUtil.AddInlineAttribute(wrapperNoninterferenceCheckerProc);

      // initialize globalSnapshotInstrumentation
      globalSnapshotInstrumentation = new GlobalSnapshotInstrumentation(civlTypeChecker);

      // initialize noninterferenceInstrumentation
      if (CommandLineOptions.Clo.TrustNoninterference)
      {
        noninterferenceInstrumentation = new NoneNoninterferenceInstrumentation();
      }
      else
      {
        noninterferenceInstrumentation = new SomeNoninterferenceInstrumentation(
          civlTypeChecker,
          linearTypeChecker,
          linearPermissionInstrumentation,
          globalSnapshotInstrumentation.OldGlobalMap,
          wrapperNoninterferenceCheckerProc);
      }
    }

    private YieldingProc GetYieldingProc(Implementation impl)
    {
      var originalImpl = (Implementation) absyMap[impl];
      return civlTypeChecker.procToYieldingProc[originalImpl.Proc];
    }

    private Implementation WrapperNoninterferenceCheckerImpl()
    {
      List<Variable> inputs = new List<Variable>();
      foreach (string domainName in linearTypeChecker.linearDomains.Keys)
      {
        inputs.Add(linearTypeChecker.LinearDomainInFormal(domainName));
      }

      foreach (Variable g in civlTypeChecker.GlobalVariables)
      {
        inputs.Add(OldGlobalFormal(g));
      }

      List<Block> blocks = new List<Block>();
      TransferCmd transferCmd = new ReturnCmd(Token.NoToken);
      if (noninterferenceCheckerDecls.Count > 0)
      {
        List<Block> blockTargets = new List<Block>();
        List<String> labelTargets = new List<String>();
        int labelCount = 0;
        foreach (Procedure proc in noninterferenceCheckerDecls.OfType<Procedure>())
        {
          List<Expr> exprSeq = new List<Expr>();
          foreach (Variable v in inputs)
          {
            exprSeq.Add(Expr.Ident(v));
          }

          CallCmd callCmd = new CallCmd(Token.NoToken, proc.Name, exprSeq, new List<IdentifierExpr>());
          callCmd.Proc = proc;
          string label = $"L_{labelCount++}";
          Block block = new Block(Token.NoToken, label, new List<Cmd> {callCmd},
            new ReturnCmd(Token.NoToken));
          labelTargets.Add(label);
          blockTargets.Add(block);
          blocks.Add(block);
        }

        transferCmd = new GotoCmd(Token.NoToken, labelTargets, blockTargets);
      }

      blocks.Insert(0, new Block(Token.NoToken, "enter", new List<Cmd>(), transferCmd));

      var yieldImpl = new Implementation(Token.NoToken, wrapperNoninterferenceCheckerProc.Name,
        new List<TypeVariable>(), inputs,
        new List<Variable>(), new List<Variable>(), blocks);
      yieldImpl.Proc = wrapperNoninterferenceCheckerProc;
      CivlUtil.AddInlineAttribute(yieldImpl);
      return yieldImpl;
    }

    private Formal OldGlobalFormal(Variable v)
    {
      return new Formal(Token.NoToken,
        new TypedIdent(Token.NoToken, $"civl_global_old_{v.Name}", v.TypedIdent.Type), true);
    }

    private void AddNoninterferenceCheckers()
    {
      if (CommandLineOptions.Clo.TrustNoninterference) return;
      foreach (var proc in civlTypeChecker.procToYieldInvariant.Keys)
      {
        var yieldInvariant = civlTypeChecker.procToYieldInvariant[proc];
        if (layerNum == yieldInvariant.LayerNum)
        {
          noninterferenceCheckerDecls.AddRange(
            NoninterferenceChecker.CreateNoninterferenceCheckers(civlTypeChecker, linearTypeChecker,
              layerNum, new Dictionary<Absy, Absy>(), proc, new List<Variable>()));
        }
      }

      foreach (var impl in absyMap.Keys.OfType<Implementation>())
      {
        var yieldingProc = GetYieldingProc(impl);
        noninterferenceCheckerDecls.AddRange(
          NoninterferenceChecker.CreateNoninterferenceCheckers(civlTypeChecker, linearTypeChecker, layerNum,
            absyMap, impl, impl.LocVars));
        if (yieldingProc is MoverProc && yieldingProc.upperLayer == layerNum) continue;
        noninterferenceCheckerDecls.AddRange(
          NoninterferenceChecker.CreateNoninterferenceCheckers(civlTypeChecker, linearTypeChecker,
            layerNum, new Dictionary<Absy, Absy>(), impl.Proc, new List<Variable>()));
      }
    }

    // Add assignment g := g for all global variables g at yielding loop heads.
    // This is to make all global variables loop targets that get havoced.
    private List<Cmd> YieldingLoopDummyAssignment()
    {
      var globals = civlTypeChecker.GlobalVariables.Select(Expr.Ident).ToList();
      var cmds = new List<Cmd>();
      if (globals.Count != 0)
      {
        cmds.Add(CmdHelper.AssignCmd(globals, globals.ToList<Expr>()));
      }

      return cmds;
    }

    private List<Cmd> InlineYieldLoopInvariants(List<CallCmd> yieldInvariants)
    {
      var inlinedYieldInvariants = new List<Cmd>();
      foreach (var callCmd in yieldInvariants)
      {
        var yieldInvariant = civlTypeChecker.procToYieldInvariant[callCmd.Proc];
        if (layerNum == yieldInvariant.LayerNum)
        {
          Dictionary<Variable, Expr> map = callCmd.Proc.InParams.Zip(callCmd.Ins)
            .ToDictionary(x => x.Item1, x => x.Item2);
          Substitution subst = Substituter.SubstitutionFromHashtable(map);
          foreach (Requires req in callCmd.Proc.Requires)
          {
            var newExpr = Substituter.Apply(subst, req.Condition);
            if (req.Free)
            {
              inlinedYieldInvariants.Add(new AssumeCmd(req.tok, newExpr, req.Attributes));
            }
            else
            {
              inlinedYieldInvariants.Add(new AssertCmd(req.tok, newExpr, req.Attributes));
            }
          }
        }
      }

      return inlinedYieldInvariants;
    }

    private Dictionary<Implementation, List<Cmd>> CreatePreconditions(
      LinearPermissionInstrumentation linearPermissionInstrumentation)
    {
      var implToInitCmds = new Dictionary<Implementation, List<Cmd>>();
      foreach (var impl in absyMap.Keys.OfType<Implementation>())
      {
        var initCmds = new List<Cmd>();
        if (civlTypeChecker.GlobalVariables.Count() > 0)
        {
          initCmds.Add(new HavocCmd(Token.NoToken,
            civlTypeChecker.GlobalVariables.Select(v => Expr.Ident(v)).ToList()));
          linearPermissionInstrumentation.DisjointnessExprs(impl, true).ForEach(
            expr => initCmds.Add(new AssumeCmd(Token.NoToken, expr)));

          Substitution procToImplInParams = Substituter.SubstitutionFromHashtable(impl.Proc.InParams
            .Zip(impl.InParams).ToDictionary(x => x.Item1, x => (Expr) Expr.Ident(x.Item2)));

          impl.Proc.Requires.ForEach(req =>
            initCmds.Add(new AssumeCmd(req.tok, Substituter.Apply(procToImplInParams, req.Condition))));

          foreach (var callCmd in GetYieldingProc(impl).yieldRequires)
          {
            var yieldInvariant = civlTypeChecker.procToYieldInvariant[callCmd.Proc];
            if (layerNum == yieldInvariant.LayerNum)
            {
              Substitution callFormalsToActuals = Substituter.SubstitutionFromHashtable(callCmd.Proc.InParams
                .Zip(callCmd.Ins)
                .ToDictionary(x => x.Item1, x => (Expr) new OldExpr(Token.NoToken, x.Item2)));
              callCmd.Proc.Requires.ForEach(req => initCmds.Add(new AssumeCmd(req.tok,
                Substituter.Apply(procToImplInParams,
                  Substituter.Apply(callFormalsToActuals, req.Condition)))));
            }
          }
        }

        implToInitCmds[impl] = initCmds;
      }

      return implToInitCmds;
    }

    private void InlineYieldRequiresAndEnsures()
    {
      foreach (var impl in absyMap.Keys.OfType<Implementation>())
      {
        var yieldingProc = GetYieldingProc(impl);
        foreach (var callCmd in yieldingProc.yieldRequires)
        {
          var yieldInvariant = civlTypeChecker.procToYieldInvariant[callCmd.Proc];
          if (layerNum == yieldInvariant.LayerNum)
          {
            Dictionary<Variable, Expr> map = callCmd.Proc.InParams.Zip(callCmd.Ins)
              .ToDictionary(x => x.Item1, x => x.Item2);
            Substitution subst = Substituter.SubstitutionFromHashtable(map);
            foreach (Requires req in callCmd.Proc.Requires)
            {
              impl.Proc.Requires.Add(new Requires(req.tok, req.Free, Substituter.Apply(subst, req.Condition),
                null,
                req.Attributes));
            }
          }
        }

        foreach (var callCmd in yieldingProc.yieldEnsures)
        {
          var yieldInvariant = civlTypeChecker.procToYieldInvariant[callCmd.Proc];
          if (layerNum == yieldInvariant.LayerNum)
          {
            Dictionary<Variable, Expr> map = callCmd.Proc.InParams.Zip(callCmd.Ins)
              .ToDictionary(x => x.Item1, x => x.Item2);
            Substitution subst = Substituter.SubstitutionFromHashtable(map);
            foreach (Requires req in callCmd.Proc.Requires)
            {
              impl.Proc.Ensures.Add(new Ensures(req.tok, req.Free, Substituter.Apply(subst, req.Condition),
                null,
                req.Attributes));
            }
          }
        }
      }
    }

    private void TransformImpls(HashSet<Procedure> yieldingProcs,
      Dictionary<Implementation, List<Cmd>> implToPreconditions)
    {
      foreach (var impl in absyMap.Keys.OfType<Implementation>())
      {
        // Add disjointness assumptions at loop headers and after each parallel call
        // for each duplicate implementation of yielding procedures.
        // Disjointness assumptions after yields are added inside TransformImpl which is called for 
        // all implementations except for a mover procedure at its disappearing layer.
        // But this is fine because a mover procedure at its disappearing layer does not have a yield in it.
        linearPermissionInstrumentation.AddDisjointnessAssumptions(impl, yieldingProcs);
        var yieldingProc = GetYieldingProc(impl);
        if (yieldingProc is MoverProc && yieldingProc.upperLayer == layerNum) continue;
        TransformImpl(impl, implToPreconditions[impl]);
      }
    }

    private void TransformImpl(Implementation impl, List<Cmd> preconditions)
    {
      // initialize refinementInstrumentation
      var yieldingProc = GetYieldingProc(impl);
      if (yieldingProc.upperLayer == this.layerNum)
      {
        refinementInstrumentation = new ActionRefinementInstrumentation(
          civlTypeChecker,
          impl,
          (Implementation) absyMap[impl],
          globalSnapshotInstrumentation.OldGlobalMap);
      }
      else
      {
        refinementInstrumentation = new RefinementInstrumentation();
      }

      DesugarConcurrency(impl, preconditions);

      impl.LocVars.AddRange(globalSnapshotInstrumentation.NewLocalVars);
      impl.LocVars.AddRange(refinementInstrumentation.NewLocalVars);
      impl.LocVars.AddRange(noninterferenceInstrumentation.NewLocalVars);
    }

    private Block CreateInitialBlock(Implementation impl, List<Cmd> preconditions)
    {
      var initCmds = new List<Cmd>(preconditions);
      initCmds.AddRange(globalSnapshotInstrumentation.CreateInitCmds());
      initCmds.AddRange(refinementInstrumentation.CreateInitCmds());
      initCmds.AddRange(noninterferenceInstrumentation.CreateInitCmds(impl));
      var gotoCmd = new GotoCmd(Token.NoToken, new List<String> {impl.Blocks[0].Label},
        new List<Block> {impl.Blocks[0]});
      return new Block(Token.NoToken, "civl_init", initCmds, gotoCmd);
    }

    private bool IsYieldingLoopHeader(Block b)
    {
      if (!absyMap.ContainsKey(b)) return false;
      var originalBlock = (Block) absyMap[b];
      if (!civlTypeChecker.yieldingLoops.ContainsKey(originalBlock)) return false;
      return civlTypeChecker.yieldingLoops[originalBlock].layers.Contains(layerNum);
    }

    private void ComputeYieldingLoops(
      Implementation impl,
      out HashSet<Block> yieldingLoopHeaders,
      out HashSet<Block> blocksInYieldingLoops)
    {
      yieldingLoopHeaders = new HashSet<Block>(impl.Blocks.Where(IsYieldingLoopHeader));

      impl.PruneUnreachableBlocks();
      impl.ComputePredecessorsForBlocks();
      var graph = Program.GraphFromImpl(impl);
      graph.ComputeLoops();
      blocksInYieldingLoops = GetBlocksInAllNaturalLoops(yieldingLoopHeaders, graph);
    }

    private HashSet<Block> GetBlocksInAllNaturalLoops(HashSet<Block> headers, Graph<Block> g)
    {
      var allBlocksInNaturalLoops = new HashSet<Block>();
      foreach (Block header in headers)
      {
        foreach (Block source in g.BackEdgeNodes(header))
        {
          g.NaturalLoops(header, source).Iter(b => allBlocksInNaturalLoops.Add(b));
        }
      }

      return allBlocksInNaturalLoops;
    }

    private void AddEdge(GotoCmd gotoCmd, Block b)
    {
      gotoCmd.labelNames.Add(b.Label);
      gotoCmd.labelTargets.Add(b);
    }

    private void DesugarConcurrency(Implementation impl, List<Cmd> preconditions)
    {
      var noninterferenceCheckerBlock = CreateNoninterferenceCheckerBlock();
      var refinementCheckerBlock = CreateRefinementCheckerBlock();
      var refinementCheckerForYieldingLoopsBlock = CreateRefinementCheckerBlockForYieldingLoops();
      var returnCheckerBlock = CreateReturnCheckerBlock();
      var returnBlock = new Block(Token.NoToken, "UnifiedReturn", new List<Cmd>(), new ReturnCmd(Token.NoToken));
      SplitBlocks(impl);

      HashSet<Block> yieldingLoopHeaders;
      HashSet<Block> blocksInYieldingLoops;
      ComputeYieldingLoops(impl, out yieldingLoopHeaders, out blocksInYieldingLoops);
      foreach (Block header in yieldingLoopHeaders)
      {
        foreach (Block pred in header.Predecessors)
        {
          var gotoCmd = pred.TransferCmd as GotoCmd;
          AddEdge(gotoCmd, noninterferenceCheckerBlock);
          if (blocksInYieldingLoops.Contains(pred))
          {
            AddEdge(gotoCmd, refinementCheckerForYieldingLoopsBlock);
          }
          else
          {
            AddEdge(gotoCmd, refinementCheckerBlock);
          }
        }

        List<Cmd> firstCmds;
        List<Cmd> secondCmds;
        SplitCmds(header.Cmds, out firstCmds, out secondCmds);
        List<Cmd> newCmds = new List<Cmd>();
        newCmds.AddRange(firstCmds);
        newCmds.AddRange(refinementInstrumentation.CreateAssumeCmds());
        newCmds.AddRange(
          InlineYieldLoopInvariants(civlTypeChecker.yieldingLoops[(Block) absyMap[header]].yieldInvariants));
        newCmds.AddRange(YieldingLoopDummyAssignment());
        newCmds.AddRange(globalSnapshotInstrumentation.CreateUpdatesToOldGlobalVars());
        newCmds.AddRange(refinementInstrumentation.CreateUpdatesToOldOutputVars());
        newCmds.AddRange(noninterferenceInstrumentation.CreateUpdatesToPermissionCollector(header));
        newCmds.AddRange(secondCmds);
        header.Cmds = newCmds;
      }

      // add jumps to noninterferenceCheckerBlock, returnBlock, and refinement blocks
      var implRefinementCheckingBlocks = new List<Block>();
      foreach (var b in impl.Blocks)
      {
        if (b.TransferCmd is GotoCmd gotoCmd)
        {
          var targetBlocks = new List<Block>();
          var addEdge = false;
          foreach (var nextBlock in gotoCmd.labelTargets)
          {
            if (nextBlock.cmds.Count > 0)
            {
              var cmd = nextBlock.cmds[0];
              if (cmd is YieldCmd)
              {
                addEdge = true;
              }
              else if (cmd is ParCallCmd parCallCmd)
              {
                foreach (var callCmd in parCallCmd.CallCmds)
                {
                  if (refinementBlocks.ContainsKey(callCmd))
                  {
                    var targetBlock = refinementBlocks[callCmd];
                    FixUpImplRefinementCheckingBlock(targetBlock,
                      IsCallMarked(callCmd)
                        ? returnCheckerBlock
                        : refinementCheckerForYieldingLoopsBlock);
                    targetBlocks.Add(targetBlock);
                    implRefinementCheckingBlocks.Add(targetBlock);
                  }
                }

                addEdge = true;
              }
            }
          }

          gotoCmd.labelNames.AddRange(targetBlocks.Select(block => block.Label));
          gotoCmd.labelTargets.AddRange(targetBlocks);
          if (addEdge)
          {
            AddEdge(gotoCmd, noninterferenceCheckerBlock);
            AddEdge(gotoCmd,
              blocksInYieldingLoops.Contains(b)
                ? refinementCheckerForYieldingLoopsBlock
                : refinementCheckerBlock);
          }
        }
        else
        {
          b.TransferCmd = new GotoCmd(b.TransferCmd.tok,
            new List<string> {returnCheckerBlock.Label, returnBlock.Label, noninterferenceCheckerBlock.Label},
            new List<Block> {returnCheckerBlock, returnBlock, noninterferenceCheckerBlock});
        }
      }

      // desugar YieldCmd, CallCmd, and ParCallCmd 
      foreach (Block b in impl.Blocks)
      {
        if (b.cmds.Count > 0)
        {
          var cmd = b.cmds[0];
          if (cmd is YieldCmd)
          {
            DesugarYieldCmdInBlock(b, blocksInYieldingLoops.Contains(b));
          }
          else if (cmd is ParCallCmd)
          {
            DesugarParCallCmdInBlock(b, blocksInYieldingLoops.Contains(b));
          }
        }
      }

      impl.Blocks.Add(noninterferenceCheckerBlock);
      impl.Blocks.Add(refinementCheckerBlock);
      impl.Blocks.Add(refinementCheckerForYieldingLoopsBlock);
      impl.Blocks.Add(returnCheckerBlock);
      impl.Blocks.Add(returnBlock);
      impl.Blocks.AddRange(implRefinementCheckingBlocks);
      impl.Blocks.Insert(0, CreateInitialBlock(impl, preconditions));
    }

    private void FixUpImplRefinementCheckingBlock(Block block, Block refinementCheckerBlock)
    {
      var newCmds = new List<Cmd>();
      if (civlTypeChecker.GlobalVariables.Count() > 0)
      {
        newCmds.Add(new HavocCmd(Token.NoToken, civlTypeChecker.GlobalVariables.Select(v => Expr.Ident(v)).ToList()));
      }

      newCmds.AddRange(refinementInstrumentation.CreateAssumeCmds());
      newCmds.AddRange(globalSnapshotInstrumentation.CreateUpdatesToOldGlobalVars());
      newCmds.AddRange(refinementInstrumentation.CreateUpdatesToOldOutputVars());
      newCmds.AddRange(block.Cmds);
      block.Cmds = newCmds;
      var gotoCmd = (GotoCmd) block.TransferCmd;
      gotoCmd.labelNames.Add(refinementCheckerBlock.Label);
      gotoCmd.labelTargets.Add(refinementCheckerBlock);
    }

    private bool IsCallMarked(CallCmd callCmd)
    {
      return callCmd.HasAttribute(CivlAttributes.REFINES);
    }

    private bool IsParCallMarked(ParCallCmd parCallCmd)
    {
      return parCallCmd.CallCmds.Any(callCmd => IsCallMarked(callCmd));
    }

    private void SplitBlocks(Implementation impl)
    {
      List<Block> newBlocks = new List<Block>();
      foreach (Block b in impl.Blocks)
      {
        var currTransferCmd = b.TransferCmd;
        int labelCount = 0;
        int lastSplitIndex = b.cmds.Count;
        for (int i = b.cmds.Count - 1; i >= 0; i--)
        {
          var split = false;
          var cmd = b.cmds[i];
          if (cmd is YieldCmd)
          {
            split = true;
          }
          else if (cmd is ParCallCmd)
          {
            split = true;
          }

          if (split)
          {
            var newBlock = new Block(b.tok, $"{b.Label}_{labelCount++}", b.cmds.GetRange(i, lastSplitIndex - i),
              currTransferCmd);
            newBlocks.Add(newBlock);
            currTransferCmd = new GotoCmd(b.tok, new List<string> {newBlock.Label},
              new List<Block> {newBlock});
            lastSplitIndex = i;
          }
        }

        b.cmds = b.cmds.GetRange(0, lastSplitIndex);
        b.TransferCmd = currTransferCmd;
      }

      impl.Blocks.AddRange(newBlocks);
    }

    private Block CreateNoninterferenceCheckerBlock()
    {
      var newCmds = new List<Cmd>();
      newCmds.AddRange(noninterferenceInstrumentation.CreateCallToYieldProc());
      newCmds.Add(new AssumeCmd(Token.NoToken, Expr.False));
      return new Block(Token.NoToken, "NoninterferenceChecker", newCmds, new ReturnCmd(Token.NoToken));
    }

    private Block CreateRefinementCheckerBlock()
    {
      var newCmds = new List<Cmd>();
      newCmds.AddRange(refinementInstrumentation.CreateAssertCmds());
      newCmds.Add(new AssumeCmd(Token.NoToken, Expr.False));
      return new Block(Token.NoToken, "RefinementChecker", newCmds, new ReturnCmd(Token.NoToken));
    }

    private Block CreateRefinementCheckerBlockForYieldingLoops()
    {
      var newCmds = new List<Cmd>();
      newCmds.AddRange(refinementInstrumentation.CreateUnchangedGlobalsAssertCmds());
      newCmds.AddRange(refinementInstrumentation.CreateUnchangedOutputsAssertCmds());
      newCmds.Add(new AssumeCmd(Token.NoToken, Expr.False));
      return new Block(Token.NoToken, "RefinementCheckerForYieldingLoops", newCmds, new ReturnCmd(Token.NoToken));
    }

    private Block CreateReturnCheckerBlock()
    {
      var returnBlockCmds = new List<Cmd>();
      returnBlockCmds.AddRange(refinementInstrumentation.CreateAssertCmds());
      returnBlockCmds.AddRange(refinementInstrumentation.CreateUpdatesToRefinementVars(false));
      returnBlockCmds.AddRange(refinementInstrumentation.CreateReturnAssertCmds());
      returnBlockCmds.Add(new AssumeCmd(Token.NoToken, Expr.False));
      return new Block(Token.NoToken, "ReturnChecker", returnBlockCmds, new ReturnCmd(Token.NoToken));
    }

    private void DesugarYieldCmdInBlock(Block block, bool isBlockInYieldingLoop)
    {
      YieldCmd yieldCmd = (YieldCmd) block.Cmds[0];
      var newCmds = new List<Cmd>();
      if (!isBlockInYieldingLoop)
      {
        newCmds.AddRange(refinementInstrumentation.CreateUpdatesToRefinementVars(false));
      }

      var yieldPredicates = new List<PredicateCmd>();
      for (int i = 1; i < block.Cmds.Count; i++)
      {
        if (block.Cmds[i] is PredicateCmd predicateCmd)
        {
          yieldPredicates.Add(predicateCmd);
        }
        else
        {
          break;
        }
      }

      newCmds.AddRange(yieldPredicates);
      if (civlTypeChecker.GlobalVariables.Count() > 0)
      {
        newCmds.Add(new HavocCmd(Token.NoToken, civlTypeChecker.GlobalVariables.Select(v => Expr.Ident(v)).ToList()));
      }

      newCmds.AddRange(refinementInstrumentation.CreateAssumeCmds());
      newCmds.AddRange(linearPermissionInstrumentation.DisjointnessAssumeCmds(yieldCmd, true));
      newCmds.AddRange(globalSnapshotInstrumentation.CreateUpdatesToOldGlobalVars());
      newCmds.AddRange(refinementInstrumentation.CreateUpdatesToOldOutputVars());
      newCmds.AddRange(noninterferenceInstrumentation.CreateUpdatesToPermissionCollector(yieldCmd));
      newCmds.AddRange(yieldPredicates.Select(x => new AssumeCmd(Token.NoToken, x.Expr)));
      var offsetAfterYieldPredicates = 1 + yieldPredicates.Count;
      newCmds.AddRange(block.cmds.GetRange(offsetAfterYieldPredicates, block.cmds.Count - offsetAfterYieldPredicates));
      block.cmds = newCmds;
    }

    private void DesugarParCallCmdInBlock(Block block, bool isBlockInYieldingLoop)
    {
      var parCallCmd = (ParCallCmd) block.Cmds[0];
      List<Cmd> newCmds = new List<Cmd>();
      if (!isBlockInYieldingLoop)
      {
        newCmds.AddRange(refinementInstrumentation.CreateUpdatesToRefinementVars(IsParCallMarked(parCallCmd)));
      }

      List<Expr> ins = new List<Expr>();
      List<IdentifierExpr> outs = new List<IdentifierExpr>();
      string procName = "ParallelCall";
      foreach (CallCmd callCmd in parCallCmd.CallCmds)
      {
        procName = procName + "_" + callCmd.Proc.Name;
        ins.AddRange(callCmd.Ins);
        outs.AddRange(callCmd.Outs);
      }

      if (!parallelCallAggregators.ContainsKey(procName))
      {
        List<Variable> inParams = new List<Variable>();
        List<Variable> outParams = new List<Variable>();
        List<Requires> requiresSeq = new List<Requires>();
        List<Ensures> ensuresSeq = new List<Ensures>();
        int count = 0;
        foreach (CallCmd callCmd in parCallCmd.CallCmds)
        {
          Dictionary<Variable, Expr> map = new Dictionary<Variable, Expr>();
          foreach (Variable x in callCmd.Proc.InParams)
          {
            Variable y = ParCallDesugarFormal(x, count, true);
            inParams.Add(y);
            map[x] = Expr.Ident(y);
          }

          foreach (Variable x in callCmd.Proc.OutParams)
          {
            Variable y = ParCallDesugarFormal(x, count, false);
            outParams.Add(y);
            map[x] = Expr.Ident(y);
          }

          Contract.Assume(callCmd.Proc.TypeParameters.Count == 0);
          Substitution subst = Substituter.SubstitutionFromHashtable(map);
          foreach (Requires req in callCmd.Proc.Requires)
          {
            requiresSeq.Add(new Requires(req.tok, req.Free, Substituter.Apply(subst, req.Condition), null,
              req.Attributes));
          }

          foreach (Ensures ens in callCmd.Proc.Ensures)
          {
            ensuresSeq.Add(new Ensures(ens.tok, ens.Free, Substituter.Apply(subst, ens.Condition), null,
              ens.Attributes));
          }

          count++;
        }

        parallelCallAggregators[procName] = new Procedure(Token.NoToken, procName,
          new List<TypeVariable>(), inParams, outParams, requiresSeq,
          civlTypeChecker.GlobalVariables.Select(v => Expr.Ident(v)).ToList(), ensuresSeq);
      }

      Procedure proc = parallelCallAggregators[procName];
      CallCmd checkerCallCmd = new CallCmd(parCallCmd.tok, proc.Name, ins, outs, parCallCmd.Attributes);
      checkerCallCmd.Proc = proc;
      newCmds.Add(checkerCallCmd);
      newCmds.AddRange(refinementInstrumentation.CreateAssumeCmds());
      newCmds.AddRange(globalSnapshotInstrumentation.CreateUpdatesToOldGlobalVars());
      newCmds.AddRange(refinementInstrumentation.CreateUpdatesToOldOutputVars());
      newCmds.AddRange(noninterferenceInstrumentation.CreateUpdatesToPermissionCollector(parCallCmd));
      newCmds.AddRange(block.cmds.GetRange(1, block.cmds.Count - 1));
      block.cmds = newCmds;
    }

    private Formal ParCallDesugarFormal(Variable v, int count, bool incoming)
    {
      return new Formal(Token.NoToken,
        new TypedIdent(Token.NoToken, $"civl_{count}_{v.Name}", v.TypedIdent.Type), incoming);
    }

    private void SplitCmds(List<Cmd> cmds, out List<Cmd> firstCmds, out List<Cmd> secondCmds)
    {
      firstCmds = new List<Cmd>();
      secondCmds = new List<Cmd>();
      var count = 0;
      bool splitDone = false;
      while (count < cmds.Count)
      {
        var cmd = cmds[count];
        if (splitDone)
        {
          secondCmds.Add(cmd);
          count++;
        }
        else if (cmd is PredicateCmd)
        {
          firstCmds.Add(cmd);
          count++;
        }
        else
        {
          splitDone = true;
        }
      }
    }

    private IEnumerable<Declaration> PendingAsyncNoninterferenceCheckers()
    {
      if (CommandLineOptions.Clo.TrustNoninterference) yield break;

      HashSet<AtomicAction> pendingAsyncsToCheck = new HashSet<AtomicAction>(
        civlTypeChecker.procToAtomicAction.Values
          .Where(a => a.layerRange.Contains(layerNum) && a.HasPendingAsyncs)
          .SelectMany(a => a.pendingAsyncs));

      foreach (var action in pendingAsyncsToCheck)
      {
        var name = $"PendingAsyncNoninterferenceChecker_{action.proc.Name}_{layerNum}";
        var inputs = action.impl.InParams;
        var outputs = action.impl.OutParams;
        var requires = action.gate.Select(a => new Requires(false, a.Expr)).ToList();
        var ensures = new List<Ensures>();
        var modifies = civlTypeChecker.GlobalVariables.Select(Expr.Ident).ToList();
        var locals = globalSnapshotInstrumentation.NewLocalVars.Union(noninterferenceInstrumentation.NewLocalVars).ToList();
        var cmds = new List<Cmd>();
        var typeVars = new List<TypeVariable>();

        cmds.AddRange(globalSnapshotInstrumentation.CreateInitCmds());
        cmds.AddRange(noninterferenceInstrumentation.CreateInitCmds(action.impl));
        cmds.Add(CmdHelper.CallCmd(action.proc, inputs, outputs));
        cmds.AddRange(noninterferenceInstrumentation.CreateCallToYieldProc());
        var blocks = new List<Block> { new Block(Token.NoToken, "entry", cmds, new ReturnCmd(Token.NoToken)) };

        var proc = new Procedure(Token.NoToken, name, typeVars, inputs, outputs, requires, modifies, ensures);
        var impl = new Implementation(Token.NoToken, name, typeVars, inputs, outputs, locals, blocks) { Proc = proc };
        yield return proc;
        yield return impl;
      }
    }
  }
}