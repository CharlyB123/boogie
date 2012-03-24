﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Boogie;
using System.Diagnostics;

namespace GPUVerify
{
    class LoopInvariantGenerator
    {
        private GPUVerifier verifier;
        private Implementation Impl;

        public LoopInvariantGenerator(GPUVerifier verifier, Implementation Impl)
        {
            this.verifier = verifier;
            this.Impl = Impl;
        }

        internal void instrument(List<Expr> UserSuppliedInvariants)
        {
                HashSet<Variable> LocalVars = new HashSet<Variable>();
                foreach (Variable v in Impl.LocVars)
                {
                    LocalVars.Add(v);
                }
                foreach (Variable v in Impl.InParams)
                {
                    LocalVars.Add(v);
                }
                foreach (Variable v in Impl.OutParams)
                {
                    LocalVars.Add(v);
                }

                AddCandidateInvariants(Impl.StructuredStmts, LocalVars, UserSuppliedInvariants, Impl);

        }

        private void AddEqualityCandidateInvariant(WhileCmd wc, string LoopPredicate, Variable v)
        {
            verifier.AddCandidateInvariant(wc,
                Expr.Eq(
                    new IdentifierExpr(wc.tok, new VariableDualiser(1, verifier.uniformityAnalyser, Impl.Name).VisitVariable(v.Clone() as Variable)),
                    new IdentifierExpr(wc.tok, new VariableDualiser(2, verifier.uniformityAnalyser, Impl.Name).VisitVariable(v.Clone() as Variable))
            ));
        }

        private void AddPredicatedEqualityCandidateInvariant(WhileCmd wc, string LoopPredicate, Variable v)
        {
            verifier.AddCandidateInvariant(wc, Expr.Imp(
                Expr.And(
                    new IdentifierExpr(wc.tok, new LocalVariable(wc.tok, new TypedIdent(wc.tok, LoopPredicate + "$1", Microsoft.Boogie.Type.Int))),
                    new IdentifierExpr(wc.tok, new LocalVariable(wc.tok, new TypedIdent(wc.tok, LoopPredicate + "$2", Microsoft.Boogie.Type.Int)))
                ),
                Expr.Eq(
                    new IdentifierExpr(wc.tok, new VariableDualiser(1, verifier.uniformityAnalyser, Impl.Name).VisitVariable(v.Clone() as Variable)),
                    new IdentifierExpr(wc.tok, new VariableDualiser(2, verifier.uniformityAnalyser, Impl.Name).VisitVariable(v.Clone() as Variable))
            )));
        }


        private void AddBarrierDivergenceCandidates(HashSet<Variable> LocalVars, Implementation Impl, WhileCmd wc)
        {

            if (CommandLineOptions.AddDivergenceCandidatesOnlyToBarrierLoops)
            {
                if (!ContainsBarrierCall(wc.Body))
                {
                    return;
                }
            }

            Debug.Assert(wc.Guard is NAryExpr);
            Debug.Assert((wc.Guard as NAryExpr).Args.Length == 2);
            Debug.Assert((wc.Guard as NAryExpr).Args[0] is IdentifierExpr);
            string LoopPredicate = ((wc.Guard as NAryExpr).Args[0] as IdentifierExpr).Name;

            LoopPredicate = LoopPredicate.Substring(0, LoopPredicate.IndexOf('$'));

            verifier.AddCandidateInvariant(wc, Expr.Eq(
                // Int type used here, but it doesn't matter as we will print and then re-parse the program
                new IdentifierExpr(wc.tok, new LocalVariable(wc.tok, new TypedIdent(wc.tok, LoopPredicate + "$1", Microsoft.Boogie.Type.Int))),
                new IdentifierExpr(wc.tok, new LocalVariable(wc.tok, new TypedIdent(wc.tok, LoopPredicate + "$2", Microsoft.Boogie.Type.Int)))
            ));

            foreach (Variable v in LocalVars)
            {
                string lv = GPUVerifier.StripThreadIdentifier(v.Name);

                if (GPUVerifier.IsPredicateOrTemp(lv))
                {
                    continue;
                }

                if (CommandLineOptions.AddDivergenceCandidatesOnlyIfModified)
                {
                    if (!GPUVerifier.ContainsNamedVariable(GetModifiedVariables(wc.Body), 
                        GPUVerifier.StripThreadIdentifier(v.Name)))
                    {
                        continue;
                    }
                }

                AddEqualityCandidateInvariant(wc, LoopPredicate, new LocalVariable(wc.tok, new TypedIdent(wc.tok, lv, Microsoft.Boogie.Type.Int)));

                if (Impl != verifier.KernelImplementation)
                {
                    AddPredicatedEqualityCandidateInvariant(wc, LoopPredicate, new LocalVariable(wc.tok, new TypedIdent(wc.tok, lv, Microsoft.Boogie.Type.Int)));
                }
            }

            if (!CommandLineOptions.FullAbstraction && CommandLineOptions.ArrayEqualities)
            {
                foreach (Variable v in verifier.NonLocalState.getAllNonLocalVariables())
                {
                    AddEqualityCandidateInvariant(wc, LoopPredicate, v);
                }
            }
        }

        private void AddCandidateInvariants(StmtList stmtList, HashSet<Variable> LocalVars, List<Expr> UserSuppliedInvariants, Implementation Impl)
        {
            foreach (BigBlock bb in stmtList.BigBlocks)
            {
                AddCandidateInvariants(bb, LocalVars, UserSuppliedInvariants, Impl);
            }
        }

        private void AddCandidateInvariants(BigBlock bb, HashSet<Variable> LocalVars, List<Expr> UserSuppliedInvariants, Implementation Impl)
        {
            if (bb.ec is WhileCmd)
            {
                WhileCmd wc = bb.ec as WhileCmd;

                AddBarrierDivergenceCandidates(LocalVars, Impl, wc);

                verifier.RaceInstrumenter.AddRaceCheckingCandidateInvariants(wc);

                AddUserSuppliedInvariants(wc, UserSuppliedInvariants, Impl);

                AddCandidateInvariants(wc.Body, LocalVars, UserSuppliedInvariants, Impl);
            }
            else if (bb.ec is IfCmd)
            {
                // We should have done predicated execution by now, so we won't have any if statements
                Debug.Assert(false);
            }
            else
            {
                Debug.Assert(bb.ec == null);
            }
        }

        private void AddUserSuppliedInvariants(WhileCmd wc, List<Expr> UserSuppliedInvariants, Implementation Impl)
        {
            foreach (Expr e in UserSuppliedInvariants)
            {
                wc.Invariants.Add(new AssertCmd(wc.tok, e));
                bool OK = verifier.ProgramIsOK(Impl);
                wc.Invariants.RemoveAt(wc.Invariants.Count - 1);
                if (OK)
                {
                    verifier.AddCandidateInvariant(wc, e);
                }
            }
        }

        private HashSet<Variable> GetModifiedVariables(StmtList stmtList)
        {
            HashSet<Variable> result = new HashSet<Variable>();

            foreach (BigBlock bb in stmtList.BigBlocks)
            {
                HashSet<Variable> resultForBlock = GetModifiedVariables(bb);
                foreach (Variable v in resultForBlock)
                {
                    result.Add(v);
                }
            }

            return result;
        }

        private HashSet<Variable> GetModifiedVariables(BigBlock bb)
        {
            HashSet<Variable> result = new HashSet<Variable>();

            foreach (Cmd c in bb.simpleCmds)
            {
                VariableSeq vars = new VariableSeq();
                c.AddAssignedVariables(vars);
                foreach (Variable v in vars)
                {
                    result.Add(v);
                }
            }

            if (bb.ec is WhileCmd)
            {
                HashSet<Variable> modifiedByLoop = GetModifiedVariables((bb.ec as WhileCmd).Body);
                foreach (Variable v in modifiedByLoop)
                {
                    result.Add(v);
                }
            }
            else if (bb.ec is IfCmd)
            {
                HashSet<Variable> modifiedByThen = GetModifiedVariables((bb.ec as IfCmd).thn);
                foreach (Variable v in modifiedByThen)
                {
                    result.Add(v);
                }

                if ((bb.ec as IfCmd).elseBlock != null)
                {
                    HashSet<Variable> modifiedByElse = GetModifiedVariables((bb.ec as IfCmd).elseBlock);
                    foreach (Variable v in modifiedByElse)
                    {
                        result.Add(v);
                    }
                }

                Debug.Assert((bb.ec as IfCmd).elseIf == null);
            }

            return result;
        }

        private bool ContainsBarrierCall(StmtList stmtList)
        {
            foreach (BigBlock bb in stmtList.BigBlocks)
            {
                if (ContainsBarrierCall(bb))
                {
                    return true;
                }
            }
            return false;
        }

        private bool ContainsBarrierCall(BigBlock bb)
        {
            foreach (Cmd c in bb.simpleCmds)
            {
                if (c is CallCmd && ((c as CallCmd).Proc == verifier.BarrierProcedure))
                {
                    return true;
                }
            }

            if (bb.ec is WhileCmd)
            {
                return ContainsBarrierCall((bb.ec as WhileCmd).Body);
            }

            Debug.Assert(bb.ec == null || bb.ec is BreakCmd);

            return false;
        }


    }
}