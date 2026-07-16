// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4;

/// <summary>Runs a test suite's cases sequentially when they cannot safely overlap.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class SequentialAttribute : Attribute;
