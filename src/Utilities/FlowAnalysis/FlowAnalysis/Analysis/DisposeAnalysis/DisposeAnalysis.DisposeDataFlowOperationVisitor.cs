﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Analyzer.Utilities;
using Analyzer.Utilities.Extensions;
using Microsoft.CodeAnalysis.FlowAnalysis.DataFlow.PointsToAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.CodeAnalysis.FlowAnalysis.DataFlow.DisposeAnalysis
{
    using DisposeAnalysisData = DictionaryAnalysisData<AbstractLocation, DisposeAbstractValue>;

    public partial class DisposeAnalysis : ForwardDataFlowAnalysis<DisposeAnalysisData, DisposeAnalysisContext, DisposeAnalysisResult, DisposeBlockAnalysisResult, DisposeAbstractValue>
    {
        /// <summary>
        /// Operation visitor to flow the dispose values across a given statement in a basic block.
        /// </summary>
        private sealed class DisposeDataFlowOperationVisitor : AbstractLocationDataFlowOperationVisitor<DisposeAnalysisData, DisposeAnalysisContext, DisposeAnalysisResult, DisposeAbstractValue>
        {
            private readonly Dictionary<IFieldSymbol, PointsToAbstractValue> _trackedInstanceFieldLocationsOpt;
            private ImmutableHashSet<INamedTypeSymbol> DisposeOwnershipTransferLikelyTypes => DataFlowAnalysisContext.DisposeOwnershipTransferLikelyTypes;

            public DisposeDataFlowOperationVisitor(DisposeAnalysisContext analysisContext)
                : base(analysisContext)
            {
                Debug.Assert(analysisContext.WellKnownTypeProvider.IDisposable != null);
                Debug.Assert(analysisContext.WellKnownTypeProvider.CollectionTypes.All(ct => ct.TypeKind == TypeKind.Interface));
                Debug.Assert(analysisContext.DisposeOwnershipTransferLikelyTypes != null);
                Debug.Assert(analysisContext.PointsToAnalysisResultOpt != null);

                if (analysisContext.TrackInstanceFields)
                {
                    _trackedInstanceFieldLocationsOpt = new Dictionary<IFieldSymbol, PointsToAbstractValue>();
                }
            }

            public override int GetHashCode()
            {
                return HashUtilities.Combine(_trackedInstanceFieldLocationsOpt.GetHashCodeOrDefault(), base.GetHashCode());
            }

            public ImmutableDictionary<IFieldSymbol, PointsToAbstractValue> TrackedInstanceFieldPointsToMap
            {
                get
                {
                    if (_trackedInstanceFieldLocationsOpt == null)
                    {
                        throw new InvalidOperationException();
                    }

                    return _trackedInstanceFieldLocationsOpt.ToImmutableDictionary();
                }
            }

            protected override DisposeAbstractValue GetAbstractDefaultValue(ITypeSymbol type) => DisposeAbstractValue.NotDisposable;

            protected override DisposeAbstractValue GetAbstractValue(AbstractLocation location) => CurrentAnalysisData.TryGetValue(location, out var value) ? value : ValueDomain.UnknownOrMayBeValue;

            protected override bool HasAnyAbstractValue(DisposeAnalysisData data) => data.Count > 0;

            protected override void SetAbstractValue(AbstractLocation location, DisposeAbstractValue value)
            {
                Debug.Assert(location.IsNull || location.LocationTypeOpt.IsDisposable(WellKnownTypeProvider.IDisposable));

                if (!location.IsNull)
                {
                    CurrentAnalysisData[location] = value;
                }
            }

            protected override void StopTrackingAbstractValue(AbstractLocation location) => CurrentAnalysisData.Remove(location);

            protected override void ResetCurrentAnalysisData() => ResetAnalysisData(CurrentAnalysisData);

            protected override DisposeAbstractValue HandleInstanceCreation(ITypeSymbol instanceType, PointsToAbstractValue instanceLocation, DisposeAbstractValue defaultValue)
            {
                defaultValue = DisposeAbstractValue.NotDisposable;

                if (!instanceType.IsDisposable(WellKnownTypeProvider.IDisposable))
                {
                    return defaultValue;
                }

                // Special case: Do not track System.Threading.Tasks.Task as you are not required to dispose them.
                if (WellKnownTypeProvider.Task != null && instanceType.DerivesFrom(WellKnownTypeProvider.Task, baseTypesOnly: true))
                {
                    return defaultValue;
                }

                SetAbstractValue(instanceLocation, DisposeAbstractValue.NotDisposed);
                return DisposeAbstractValue.NotDisposed;
            }

            private void HandleDisposingOperation(IOperation disposingOperation, IOperation disposedInstance)
            {
                if (disposedInstance.Type?.IsDisposable(WellKnownTypeProvider.IDisposable) == false)
                {
                    return;
                }

                PointsToAbstractValue instanceLocation = GetPointsToAbstractValue(disposedInstance);
                foreach (AbstractLocation location in instanceLocation.Locations)
                {
                    if (CurrentAnalysisData.TryGetValue(location, out DisposeAbstractValue currentDisposeValue))
                    {
                        DisposeAbstractValue disposeValue = currentDisposeValue.WithNewDisposingOperation(disposingOperation);
                        SetAbstractValue(location, disposeValue);
                    }
                }
            }

            private void HandlePossibleInvalidatingOperation(IOperation invalidatedInstance)
            {
                PointsToAbstractValue instanceLocation = GetPointsToAbstractValue(invalidatedInstance);
                foreach (AbstractLocation location in instanceLocation.Locations)
                {
                    if (CurrentAnalysisData.TryGetValue(location, out DisposeAbstractValue currentDisposeValue) &&
                        currentDisposeValue.Kind != DisposeAbstractValueKind.NotDisposable)
                    {
                        SetAbstractValue(location, DisposeAbstractValue.Invalid);
                    }
                }
            }

            private void HandlePossibleEscapingOperation(IOperation escapingOperation, ImmutableHashSet<AbstractLocation> escapedLocations)
            {
                foreach (AbstractLocation escapedLocation in escapedLocations)
                {
                    if (CurrentAnalysisData.TryGetValue(escapedLocation, out DisposeAbstractValue currentDisposeValue) &&
                        currentDisposeValue.Kind != DisposeAbstractValueKind.Unknown)
                    {
                        DisposeAbstractValue newDisposeValue = currentDisposeValue.WithNewEscapingOperation(escapingOperation);
                        SetAbstractValue(escapedLocation, newDisposeValue);
                    }
                }
            }

            protected override void SetAbstractValueForArrayElementInitializer(IArrayCreationOperation arrayCreation, ImmutableArray<AbstractIndex> indices, ITypeSymbol elementType, IOperation initializer, DisposeAbstractValue value)
            {
                // Escaping from array element assignment is handled in PointsTo analysis.
                // We do not need to do anything here.
            }

            protected override void SetAbstractValueForAssignment(IOperation target, IOperation assignedValueOperation, DisposeAbstractValue assignedValue, bool mayBeAssignment = false)
            {
                // Assignments should automatically transfer PointsTo value.
                // We do not need to do anything here.
            }

            protected override void SetAbstractValueForTupleElementAssignment(AnalysisEntity tupleElementEntity, IOperation assignedValueOperation, DisposeAbstractValue assignedValue)
            {
                // Assigning to tuple elements should automatically transfer PointsTo value.
                // We do not need to do anything here.
            }

            protected override void SetValueForParameterPointsToLocationOnEntry(IParameterSymbol parameter, PointsToAbstractValue pointsToAbstractValue)
            {
                if (DisposeOwnershipTransferLikelyTypes.Contains(parameter.Type))
                {
                    SetAbstractValue(pointsToAbstractValue, DisposeAbstractValue.NotDisposed);
                }
            }

            protected override void EscapeValueForParameterPointsToLocationOnExit(IParameterSymbol parameter, AnalysisEntity analysisEntity, ImmutableHashSet<AbstractLocation> escapedLocations)
            {
                Debug.Assert(!escapedLocations.IsEmpty);
                Debug.Assert(parameter.RefKind != RefKind.None);
                var escapedDisposableLocations = escapedLocations.Where(l => l.LocationTypeOpt?.IsDisposable(WellKnownTypeProvider.IDisposable) == true);
                SetAbstractValue(escapedDisposableLocations, ValueDomain.UnknownOrMayBeValue);
            }

            protected override DisposeAbstractValue ComputeAnalysisValueForEscapedRefOrOutArgument(IArgumentOperation operation, DisposeAbstractValue defaultValue)
            {
                Debug.Assert(operation.Parameter.RefKind == RefKind.Ref || operation.Parameter.RefKind == RefKind.Out);

                // Special case: don't flag "out" arguments for "bool TryGetXXX(..., out value)" invocations.
                if (operation.Parent is IInvocationOperation invocation &&
                    invocation.TargetMethod.ReturnType.SpecialType == SpecialType.System_Boolean &&
                    invocation.TargetMethod.Name.StartsWith("TryGet", StringComparison.Ordinal) &&
                    invocation.Arguments[invocation.Arguments.Length - 1] == operation)
                {
                    return DisposeAbstractValue.NotDisposable;
                }

                return base.ComputeAnalysisValueForEscapedRefOrOutArgument(operation, defaultValue);
            }

            protected override DisposeAnalysisData MergeAnalysisData(DisposeAnalysisData value1, DisposeAnalysisData value2)
                => DisposeAnalysisDomainInstance.Merge(value1, value2);
            protected override DisposeAnalysisData GetClonedAnalysisData(DisposeAnalysisData analysisData)
                => GetClonedAnalysisDataHelper(CurrentAnalysisData);
            public override DisposeAnalysisData GetEmptyAnalysisData()
                => GetEmptyAnalysisDataHelper();
            protected override DisposeAnalysisData GetExitBlockOutputData(DisposeAnalysisResult analysisResult)
                => GetClonedAnalysisDataHelper(analysisResult.ExitBlockOutput.Data);
            protected override bool Equals(DisposeAnalysisData value1, DisposeAnalysisData value2)
                => EqualsHelper(value1, value2);

            #region Visitor methods
            public override DisposeAbstractValue DefaultVisit(IOperation operation, object argument)
            {
                _ = base.DefaultVisit(operation, argument);
                return DisposeAbstractValue.NotDisposable;
            }

            public override DisposeAbstractValue Visit(IOperation operation, object argument)
            {
                var value = base.Visit(operation, argument);
                HandlePossibleEscapingOperation(operation, GetEscapedLocations(operation));
                return value;
            }

            // FxCop compat: Catches things like static calls to File.Open() and Create()
            private static bool IsDisposableCreationSpecialCase(IMethodSymbol targetMethod)
                => targetMethod.IsStatic &&
                   (targetMethod.Name.StartsWith("create", StringComparison.OrdinalIgnoreCase) ||
                    targetMethod.Name.StartsWith("open", StringComparison.OrdinalIgnoreCase));

            public override DisposeAbstractValue VisitInvocation_NonLambdaOrDelegateOrLocalFunction(
                IMethodSymbol targetMethod,
                IOperation instance,
                ImmutableArray<IArgumentOperation> arguments,
                bool invokedAsDelegate,
                IOperation originaOperation,
                DisposeAbstractValue defaultValue)
            {
                var value = base.VisitInvocation_NonLambdaOrDelegateOrLocalFunction(targetMethod, instance,
                    arguments, invokedAsDelegate, originaOperation, defaultValue);

                var disposeMethodKind = targetMethod.GetDisposeMethodKind(WellKnownTypeProvider.IDisposable, WellKnownTypeProvider.Task);
                switch (disposeMethodKind)
                {
                    case DisposeMethodKind.Dispose:
                    case DisposeMethodKind.DisposeBool:
                    case DisposeMethodKind.DisposeAsync:
                        HandleDisposingOperation(originaOperation, instance);
                        break;

                    case DisposeMethodKind.Close:
                        // FxCop compat: Calling "this.Close" shouldn't count as disposing the object within the implementation of Dispose.
                        if (instance?.Kind != OperationKind.InstanceReference)
                        {
                            goto case DisposeMethodKind.Dispose;
                        }
                        break;

                    default:
                        // FxCop compat: Catches things like static calls to File.Open() and Create()
                        if (IsDisposableCreationSpecialCase(targetMethod))
                        {
                            var instanceLocation = GetPointsToAbstractValue(originaOperation);
                            return HandleInstanceCreation(originaOperation.Type, instanceLocation, value);
                        }

                        break;
                }

                return value;
            }

            protected override void ApplyInterproceduralAnalysisResult(DisposeAnalysisData resultData, bool isLambdaOrLocalFunction, DisposeAnalysisResult interproceduralResult)
            {
                base.ApplyInterproceduralAnalysisResult(resultData, isLambdaOrLocalFunction, interproceduralResult);

                // Apply the tracked instance field locations from interprocedural analysis.
                if (_trackedInstanceFieldLocationsOpt != null)
                {
                    foreach (var (field, pointsToValue) in interproceduralResult.TrackedInstanceFieldPointsToMap)
                    {
                        if (!_trackedInstanceFieldLocationsOpt.ContainsKey(field))
                        {
                            _trackedInstanceFieldLocationsOpt.Add(field, pointsToValue);
                        }
                    }
                }
            }

            protected override void PostProcessArgument(IArgumentOperation operation, bool isEscaped)
            {
                base.PostProcessArgument(operation, isEscaped);
                if (isEscaped)
                {
                    PostProcessEscapedArgument();
                }

                return;

                // Local functions.
                void PostProcessEscapedArgument()
                {
                    if (operation.Parameter.Type.IsDisposable(WellKnownTypeProvider.IDisposable))
                    {
                        // Discover if a disposable object is being passed into the creation method for this new disposable object
                        // and if the new disposable object assumes ownership of that passed in disposable object.
                        if ((operation.Parent is IObjectCreationOperation ||
                             operation.Parent is IInvocationOperation invocation && IsDisposableCreationSpecialCase(invocation.TargetMethod)) &&
                            DisposeOwnershipTransferLikelyTypes.Contains(operation.Parameter.Type))
                        {
                            var pointsToValue = GetPointsToAbstractValue(operation.Value);
                            HandlePossibleEscapingOperation(operation, pointsToValue.Locations);
                        }
                    }
                }
            }

            public override DisposeAbstractValue VisitFieldReference(IFieldReferenceOperation operation, object argument)
            {
                var value = base.VisitFieldReference(operation, argument);
                if (_trackedInstanceFieldLocationsOpt != null &&
                    !operation.Field.IsStatic &&
                    operation.Instance?.Kind == OperationKind.InstanceReference)
                {
                    if (!_trackedInstanceFieldLocationsOpt.TryGetValue(operation.Field, out _))
                    {
                        var pointsToAbstractValue = GetPointsToAbstractValue(operation);
                        if (HandleInstanceCreation(operation.Type, pointsToAbstractValue, DisposeAbstractValue.NotDisposable) != DisposeAbstractValue.NotDisposable)
                        {
                            _trackedInstanceFieldLocationsOpt.Add(operation.Field, pointsToAbstractValue);
                        }
                    }
                }

                return value;
            }

            public override DisposeAbstractValue VisitBinaryOperatorCore(IBinaryOperation operation, object argument)
            {
                var value = base.VisitBinaryOperatorCore(operation, argument);

                // Handle null-check for a disposable symbol on a control flow branch.
                //     var x = flag ? new Disposable() : null;
                //     if (x == null)
                //     {
                //         // Disposable allocation above cannot exist on this code path.
                //     }
                //

                // if (x == null)
                // {
                //      // This code path
                // }
                var isNullEqualsOnWhenTrue = FlowBranchConditionKind == ControlFlowConditionKind.WhenTrue &&
                    (operation.OperatorKind == BinaryOperatorKind.Equals || operation.OperatorKind == BinaryOperatorKind.ObjectValueEquals);

                // if (x != null) { ... }
                // else
                // {
                //      // This code path
                // }
                var isNullNotEqualsOnWhenFalse = FlowBranchConditionKind == ControlFlowConditionKind.WhenFalse &&
                    (operation.OperatorKind == BinaryOperatorKind.NotEquals || operation.OperatorKind == BinaryOperatorKind.ObjectValueNotEquals);

                if (isNullEqualsOnWhenTrue || isNullNotEqualsOnWhenFalse)
                {
                    if (GetNullAbstractValue(operation.RightOperand) == NullAbstractValue.Null)
                    {
                        // if (x == null)
                        HandlePossibleInvalidatingOperation(operation.LeftOperand);
                    }
                    else if (GetNullAbstractValue(operation.LeftOperand) == NullAbstractValue.Null)
                    {
                        // if (null == x)
                        HandlePossibleInvalidatingOperation(operation.RightOperand);
                    }
                }

                return value;
            }

            public override DisposeAbstractValue VisitIsNull(IIsNullOperation operation, object argument)
            {
                var value = base.VisitIsNull(operation, argument);

                // Handle null-check for a disposable symbol on a control flow branch.
                // See comments in VisitBinaryOperatorCore override above for further details.
                if (FlowBranchConditionKind == ControlFlowConditionKind.WhenTrue)
                {
                    HandlePossibleInvalidatingOperation(operation.Operand);
                }

                return value;
            }

            #endregion
        }
    }
}
