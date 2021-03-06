﻿using Biohazrd.Transformation;
using Biohazrd.Transformation.Common;
using ClangSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using ClangType = ClangSharp.Type;

namespace Biohazrd.CSharp
{
    public class CSharpTypeReductionTransformation : TypeReductionTransformation
    {
        private ConcurrentDictionary<(ClangType Type, long ElementCount), ConstantArrayTypeDeclaration> ConstantArrayTypes = new();
        private ConcurrentBag<ConstantArrayTypeDeclaration> NewConstantArrayTypes = new();
        private object ConstantArrayTypeCreationLock = new();

        public int ConstantArrayTypesCreated { get; private set; }

        protected override TranslatedLibrary PreTransformLibrary(TranslatedLibrary library)
        {
            Debug.Assert(ConstantArrayTypes.Count == 0 && NewConstantArrayTypes.Count == 0, "There should not be any constant array types at this point.");
            ConstantArrayTypes.Clear();
            NewConstantArrayTypes.Clear();
            ConstantArrayTypesCreated = 0;

            // We only look for constant array type declarations at the root of the library since that's were we add them
            foreach (ConstantArrayTypeDeclaration existingDeclaration in library.Declarations.OfType<ConstantArrayTypeDeclaration>())
            {
                bool success = ConstantArrayTypes.TryAdd((existingDeclaration.OriginalClangElementType, existingDeclaration.ElementCount), existingDeclaration);
                Debug.Assert(success, $"{existingDeclaration} must successfully add to the lookup dictionary.");
            }

            return library;
        }

        protected override TranslatedLibrary PostTransformLibrary(TranslatedLibrary library)
        {
            library = library with
            {
                Declarations = library.Declarations.AddRange(NewConstantArrayTypes.OrderBy(t => t.Name))
            };

            ConstantArrayTypesCreated = NewConstantArrayTypes.Count;
            ConstantArrayTypes.Clear();
            NewConstantArrayTypes.Clear();

            return base.PostTransformLibrary(library);
        }

        private ConstantArrayTypeDeclaration GetOrCreateConstantArrayTypeDeclaration(ConstantArrayType constantArrayType)
        {
            (ClangType Type, long ElementCount) key = (constantArrayType.ElementType, constantArrayType.Size);
            ConstantArrayTypeDeclaration? result;

            // Check if we have a cached instance of the constant array type
            // Note: We cannot use GetOrAdd here because we need to add the value to NewConstantArrayTypes too and it doesn't happen atomically
            if (ConstantArrayTypes.TryGetValue(key, out result))
            { return result; }

            // Acquire the creation lock
            bool addSuccess = false;
            lock (ConstantArrayTypeCreationLock)
            {
                // Check again in case another thread added the type while we were waiting for the lock
                if (ConstantArrayTypes.TryGetValue(key, out result))
                { return result; }

                // Create the new declaration
                result = new ConstantArrayTypeDeclaration(constantArrayType);

                // Add the new declaration to the lookup dictionary
                addSuccess = ConstantArrayTypes.TryAdd(key, result);
            }

            // Finish add actions outside of the lock
            // (We can add to the bag outside the lock because it is only read at the very end of the transformation.)
            Debug.Assert(addSuccess, $"{constantArrayType} should've been added successfully.");
            NewConstantArrayTypes.Add(result);

            // Return the result
            return result;
        }

        protected override TypeTransformationResult TransformClangTypeReference(TypeTransformationContext context, ClangTypeReference type)
        {
            switch (type.ClangType)
            {
                case ConstantArrayType constantArrayType:
                    return GetOrCreateConstantArrayTypeDeclaration(constantArrayType).ThisTypeReference;
                default:
                    return base.TransformClangTypeReference(context, type);
            }
        }
    }
}
