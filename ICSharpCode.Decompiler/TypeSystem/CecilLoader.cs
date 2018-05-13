// Copyright (c) 2010-2013 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using ICSharpCode.Decompiler.TypeSystem.Implementation;
using ICSharpCode.Decompiler.Util;
using Mono.Cecil;

namespace ICSharpCode.Decompiler.TypeSystem
{
	using BlobReader = BlobReader;

	/// <summary>
	/// Allows loading an IProjectContent from an already compiled assembly.
	/// </summary>
	/// <remarks>Instance methods are not thread-safe; you need to create multiple instances of CecilLoader
	/// if you want to load multiple project contents in parallel.</remarks>
	public sealed class CecilLoader : AssemblyLoader
	{
		#region Options
		// Most options are defined in the AssemblyLoader base class

		/// <summary>
		/// Specifies whether to use lazy loading. The default is false.
		/// If this property is set to true, the CecilLoader will not copy all the relevant information
		/// out of the Cecil object model, but will maintain references to the Cecil objects.
		/// This speeds up the loading process and avoids loading unnecessary information, but it causes
		/// the Cecil objects to stay in memory (which can significantly increase memory usage).
		/// It also prevents serialization of the Cecil-loaded type system.
		/// </summary>
		/// <remarks>
		/// Because the type system can be used on multiple threads, but Cecil is not
		/// thread-safe for concurrent read access, the CecilLoader will lock on the <see cref="ModuleDefinition"/> instance
		/// for every delay-loading operation.
		/// If you access the Cecil objects directly in your application, you may need to take the same lock.
		/// </remarks>
		public bool LazyLoad { get; set; }

		/// <summary>
		/// Gets/Sets whether to use the <c>dynamic</c> type.
		/// </summary>
		public bool UseDynamicType { get; set; } = true;

		/// <summary>
		/// Gets/Sets whether to use the tuple types.
		/// </summary>
		public bool UseTupleTypes { get; set; } = true;

		/// <summary>
		/// This delegate gets executed whenever an entity was loaded.
		/// </summary>
		/// <remarks>
		/// This callback may be to build a dictionary that maps between
		/// entities and cecil objects.
		/// Warning: if delay-loading is used and the type system is accessed by multiple threads,
		/// the callback may be invoked concurrently on multiple threads.
		/// </remarks>
		public Action<IUnresolvedEntity, MemberReference> OnEntityLoaded { get; set; }

		/// <summary>
		/// Specifies whether method names of explicit interface-implementations should be shortened.
		/// </summary>
		/// <remarks>This is important when working with parser-initialized type-systems in order to be consistent.</remarks>
		public bool ShortenInterfaceImplNames { get; set; } = true;
		#endregion

		ModuleDefinition currentModule;
		DefaultUnresolvedAssembly currentAssembly;

		/// <summary>
		/// Initializes a new instance of the <see cref="CecilLoader"/> class.
		/// </summary>
		public CecilLoader()
		{
		}

		/// <summary>
		/// Creates a nested CecilLoader for lazy-loading.
		/// </summary>
		private CecilLoader(CecilLoader loader)
		{
			// use a shared typeSystemTranslationTable
			this.IncludeInternalMembers = loader.IncludeInternalMembers;
			this.UseDynamicType = loader.UseDynamicType;
			this.UseTupleTypes = loader.UseTupleTypes;
			this.LazyLoad = loader.LazyLoad;
			this.OnEntityLoaded = loader.OnEntityLoaded;
			this.ShortenInterfaceImplNames = loader.ShortenInterfaceImplNames;
			this.currentModule = loader.currentModule;
			this.currentAssembly = loader.currentAssembly;
			// don't use interning - the interning provider is most likely not thread-safe
			this.interningProvider = InterningProvider.Dummy;
			// don't use cancellation for delay-loaded members
		}

		#region Load From AssemblyDefinition
		/// <summary>
		/// Loads the assembly definition into a project content.
		/// </summary>
		/// <returns>Unresolved type system representing the assembly</returns>
		public IUnresolvedAssembly LoadAssembly(AssemblyDefinition assemblyDefinition)
		{
			if (assemblyDefinition == null)
				throw new ArgumentNullException("assemblyDefinition");
			return LoadModule(assemblyDefinition.MainModule);
		}

		/// <summary>
		/// Loads the module definition into a project content.
		/// </summary>
		/// <returns>Unresolved type system representing the assembly</returns>
		public IUnresolvedAssembly LoadModule(ModuleDefinition moduleDefinition)
		{
			if (moduleDefinition == null)
				throw new ArgumentNullException("moduleDefinition");

			this.currentModule = moduleDefinition;

			// Read assembly and module attributes
			IList<IUnresolvedAttribute> assemblyAttributes = new List<IUnresolvedAttribute>();
			IList<IUnresolvedAttribute> moduleAttributes = new List<IUnresolvedAttribute>();
			AssemblyDefinition assemblyDefinition = moduleDefinition.Assembly;
			if (assemblyDefinition != null) {
				AddAttributes(assemblyDefinition, assemblyAttributes);
			}
			AddAttributes(moduleDefinition, moduleAttributes);

			assemblyAttributes = interningProvider.InternList(assemblyAttributes);
			moduleAttributes = interningProvider.InternList(moduleAttributes);

			this.currentAssembly = new DefaultUnresolvedAssembly(assemblyDefinition != null ? assemblyDefinition.Name.FullName : moduleDefinition.Name);
			currentAssembly.Location = moduleDefinition.FileName;
			currentAssembly.AssemblyAttributes.AddRange(assemblyAttributes);
			currentAssembly.ModuleAttributes.AddRange(assemblyAttributes);

			// Register type forwarders:
			foreach (ExportedType type in moduleDefinition.ExportedTypes) {
				if (type.IsForwarder) {
					int typeParameterCount;
					string ns = type.Namespace;
					string name = ReflectionHelper.SplitTypeParameterCountFromReflectionName(type.Name, out typeParameterCount);
					ns = interningProvider.Intern(ns);
					name = interningProvider.Intern(name);
					var typeRef = new GetClassTypeReference(GetAssemblyReference(type.Scope), ns, name, typeParameterCount);
					typeRef = interningProvider.Intern(typeRef);
					var key = new TopLevelTypeName(ns, name, typeParameterCount);
					currentAssembly.AddTypeForwarder(key, typeRef);
				}
			}

			// Create and register all types:
			CecilLoader cecilLoaderCloneForLazyLoading = LazyLoad ? new CecilLoader(this) : null;
			List<TypeDefinition> cecilTypeDefs = new List<TypeDefinition>();
			List<DefaultUnresolvedTypeDefinition> typeDefs = new List<DefaultUnresolvedTypeDefinition>();
			foreach (TypeDefinition td in moduleDefinition.Types) {
				this.CancellationToken.ThrowIfCancellationRequested();
				if (this.IncludeInternalMembers || (td.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public) {
					string name = td.Name;
					if (name.Length == 0)
						continue;

					if (this.LazyLoad) {
						var t = new LazyCecilTypeDefinition(cecilLoaderCloneForLazyLoading, td);
						currentAssembly.AddTypeDefinition(t);
						RegisterCecilObject(t, td);
					} else {
						var t = CreateTopLevelTypeDefinition(td);
						currentAssembly.AddTypeDefinition(t);
						cecilTypeDefs.Add(td);
						typeDefs.Add(t);
						// The registration will happen after the members are initialized
					}
				}
			}
			// Initialize the type's members (but only if not lazy-init)
			for (int i = 0; i < typeDefs.Count; i++) {
				InitTypeDefinition(cecilTypeDefs[i], typeDefs[i]);
			}

			// Freezing the assembly here is important:
			// otherwise it will be frozen when a compilation is first created
			// from it. But freezing has the effect of changing some collection instances
			// (to ReadOnlyCollection). This hidden mutation was causing a crash
			// when the FastSerializer was saving the assembly at the same time as
			// the first compilation was created from it.
			// By freezing the assembly now, we ensure it is usable on multiple
			// threads without issues.
			currentAssembly.Freeze();

			var result = this.currentAssembly;
			this.currentAssembly = null;
			this.currentModule = null;
			return result;
		}

		/// <summary>
		/// Sets the current module.
		/// This causes ReadTypeReference() to use <see cref="DefaultAssemblyReference.CurrentAssembly"/> for references
		/// in that module.
		/// </summary>
		public void SetCurrentModule(ModuleDefinition module)
		{
			this.currentModule = module;
		}

		/// <summary>
		/// Loads a type from Cecil.
		/// </summary>
		/// <param name="typeDefinition">The Cecil TypeDefinition.</param>
		/// <returns>ITypeDefinition representing the Cecil type.</returns>
		public IUnresolvedTypeDefinition LoadType(TypeDefinition typeDefinition)
		{
			if (typeDefinition == null)
				throw new ArgumentNullException("typeDefinition");
			var td = CreateTopLevelTypeDefinition(typeDefinition);
			InitTypeDefinition(typeDefinition, td);
			return td;
		}
		#endregion

		#region Load Assembly From Disk
		public override IUnresolvedAssembly LoadAssemblyFile(string fileName)
		{
			if (fileName == null)
				throw new ArgumentNullException("fileName");
			var param = new ReaderParameters { AssemblyResolver = new DummyAssemblyResolver() };
			using (ModuleDefinition module = ModuleDefinition.ReadModule(fileName, param)) {
				return LoadModule(module);
			}
		}

		// used to prevent Cecil from loading referenced assemblies
		sealed class DummyAssemblyResolver : IAssemblyResolver
		{
			public AssemblyDefinition Resolve(AssemblyNameReference name)
			{
				return null;
			}

			public AssemblyDefinition Resolve(string fullName)
			{
				return null;
			}

			public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
			{
				return null;
			}

			public AssemblyDefinition Resolve(string fullName, ReaderParameters parameters)
			{
				return null;
			}

			public void Dispose()
			{
			}
		}
		#endregion

		#region Read Type Reference
		/// <summary>
		/// Reads a type reference.
		/// </summary>
		/// <param name="type">The Cecil type reference that should be converted into
		/// a type system type reference.</param>
		/// <param name="typeAttributes">Attributes associated with the Cecil type reference.
		/// This is used to support the 'dynamic' type.</param>
		/// <param name="isFromSignature">Whether this TypeReference is from a context where
		/// IsValueType is set correctly.</param>
		public ITypeReference ReadTypeReference(TypeReference type, ICustomAttributeProvider typeAttributes = null, bool isFromSignature = false)
		{
			int dynamicTypeIndex = 0;
			int tupleTypeIndex = 0;
			return CreateType(type, typeAttributes, ref dynamicTypeIndex, ref tupleTypeIndex, isFromSignature);
		}

		ITypeReference CreateType(TypeReference type, ICustomAttributeProvider typeAttributes, ref int dynamicTypeIndex, ref int tupleTypeIndex, bool isFromSignature)
		{
			if (type == null) {
				return SpecialType.UnknownType;
			}

			switch (type.MetadataType) {
				case MetadataType.Void:
					return KnownTypeReference.Void;
				case MetadataType.Boolean:
					return KnownTypeReference.Boolean;
				case MetadataType.Char:
					return KnownTypeReference.Char;
				case MetadataType.SByte:
					return KnownTypeReference.SByte;
				case MetadataType.Byte:
					return KnownTypeReference.Byte;
				case MetadataType.Int16:
					return KnownTypeReference.Int16;
				case MetadataType.UInt16:
					return KnownTypeReference.UInt16;
				case MetadataType.Int32:
					return KnownTypeReference.Int32;
				case MetadataType.UInt32:
					return KnownTypeReference.UInt32;
				case MetadataType.Int64:
					return KnownTypeReference.Int64;
				case MetadataType.UInt64:
					return KnownTypeReference.UInt64;
				case MetadataType.Single:
					return KnownTypeReference.Single;
				case MetadataType.Double:
					return KnownTypeReference.Double;
				case MetadataType.String:
					return KnownTypeReference.String;
				case MetadataType.Pointer:
					dynamicTypeIndex++;
					return interningProvider.Intern(
						new PointerTypeReference(
							CreateType(
								(type as Mono.Cecil.PointerType).ElementType,
								typeAttributes, ref dynamicTypeIndex, ref tupleTypeIndex, isFromSignature: true)));
				case MetadataType.ByReference:
					dynamicTypeIndex++;
					return interningProvider.Intern(
						new ByReferenceTypeReference(
							CreateType(
								(type as Mono.Cecil.ByReferenceType).ElementType,
								typeAttributes, ref dynamicTypeIndex, ref tupleTypeIndex, isFromSignature: true)));
				case MetadataType.Var:
					return TypeParameterReference.Create(SymbolKind.TypeDefinition, ((GenericParameter)type).Position);
				case MetadataType.MVar:
					return TypeParameterReference.Create(SymbolKind.Method, ((GenericParameter)type).Position);
				case MetadataType.Array:
					dynamicTypeIndex++;
					return interningProvider.Intern(
						new ArrayTypeReference(
							CreateType(
								(type as Mono.Cecil.ArrayType).ElementType,
								typeAttributes, ref dynamicTypeIndex, ref tupleTypeIndex, isFromSignature: true),
							(type as Mono.Cecil.ArrayType).Rank));
				case MetadataType.GenericInstance:
					GenericInstanceType gType = (GenericInstanceType)type;
					ITypeReference baseType = CreateType(gType.ElementType, typeAttributes, ref dynamicTypeIndex, ref tupleTypeIndex, isFromSignature: true);
					if (UseTupleTypes && IsValueTuple(gType, out int tupleCardinality)) {
						if (tupleCardinality > 1) {
							var elementNames = GetTupleElementNames(typeAttributes, tupleTypeIndex, tupleCardinality);
							tupleTypeIndex += tupleCardinality;
							ITypeReference[] elementTypeRefs = new ITypeReference[tupleCardinality];
							int outPos = 0;
							do {
								int normalArgCount = Math.Min(gType.GenericArguments.Count, TupleType.RestPosition - 1);
								for (int i = 0; i < normalArgCount; i++) {
									dynamicTypeIndex++;
									elementTypeRefs[outPos++] = CreateType(gType.GenericArguments[i], typeAttributes, ref dynamicTypeIndex, ref tupleTypeIndex, isFromSignature: true);
								}
								if (gType.GenericArguments.Count == TupleType.RestPosition) {
									gType = (GenericInstanceType)gType.GenericArguments.Last();
									dynamicTypeIndex++;
									if (IsValueTuple(gType, out int nestedCardinality)) {
										tupleTypeIndex += nestedCardinality;
									} else {
										Debug.Fail("TRest should be another value tuple");
									}
								} else {
									gType = null;
								}
							} while (gType != null);
							return new TupleTypeReference(elementTypeRefs.ToImmutableArray(), elementNames);
						} else {
							// C# doesn't have syntax for tuples of cardinality <= 1
							tupleTypeIndex += tupleCardinality;
						}
					}
					ITypeReference[] para = new ITypeReference[gType.GenericArguments.Count];
					for (int i = 0; i < para.Length; ++i) {
						dynamicTypeIndex++;
						para[i] = CreateType(gType.GenericArguments[i], typeAttributes, ref dynamicTypeIndex, ref tupleTypeIndex, isFromSignature: true);
					}
					return interningProvider.Intern(new ParameterizedTypeReference(baseType, para));
				case MetadataType.IntPtr:
					return KnownTypeReference.IntPtr;
				case MetadataType.UIntPtr:
					return KnownTypeReference.UIntPtr;
				case MetadataType.FunctionPointer:
					// C# and the NR typesystem don't support function pointer types.
					// Function pointer types map to StackType.I, so we'll use IntPtr instead.
					return KnownTypeReference.IntPtr;
				case MetadataType.Object:
					if (UseDynamicType && HasDynamicAttribute(typeAttributes, dynamicTypeIndex)) {
						return SpecialType.Dynamic;
					} else {
						return KnownTypeReference.Object;
					}
				case MetadataType.RequiredModifier:
				case MetadataType.OptionalModifier:
					// we don't store modopts/modreqs in the NR type system
					return CreateType(((TypeSpecification)type).ElementType, typeAttributes, ref dynamicTypeIndex, ref tupleTypeIndex, isFromSignature: true);
				case MetadataType.Sentinel:
					return SpecialType.ArgList;
				case MetadataType.Pinned:
					return CreateType(((PinnedType)type).ElementType, typeAttributes, ref dynamicTypeIndex, ref tupleTypeIndex, isFromSignature: true);
			}
			// valuetype/class/typedbyreference
			if (type is TypeDefinition) {
				return new TypeDefTokenTypeReference(type.MetadataToken);
			}
			// type.IsValueType is only reliable if we got this TypeReference from a signature,
			// or if it's a TypeSpecification.
			bool? isReferenceType = isFromSignature ? (bool?)!type.IsValueType : null;
			if (type.IsNested) {
				ITypeReference typeRef = CreateType(type.DeclaringType, typeAttributes, ref dynamicTypeIndex, ref tupleTypeIndex, isFromSignature);
				int partTypeParameterCount;
				string namepart = ReflectionHelper.SplitTypeParameterCountFromReflectionName(type.Name, out partTypeParameterCount);
				namepart = interningProvider.Intern(namepart);
				return interningProvider.Intern(new NestedTypeReference(typeRef, namepart, partTypeParameterCount, isReferenceType));
			} else {
				string ns = interningProvider.Intern(type.Namespace ?? string.Empty);
				string name = type.Name;
				if (name == null)
					throw new InvalidOperationException("type.Name returned null. Type: " + type.ToString());

				if (UseDynamicType && name == "Object" && ns == "System" && HasDynamicAttribute(typeAttributes, dynamicTypeIndex)) {
					return SpecialType.Dynamic;
				}
				int typeParameterCount;
				name = ReflectionHelper.SplitTypeParameterCountFromReflectionName(name, out typeParameterCount);
				name = interningProvider.Intern(name);
				return interningProvider.Intern(new GetClassTypeReference(
					GetAssemblyReference(type.Scope), ns, name, typeParameterCount,
					isReferenceType));
			}
		}

		static bool IsValueTuple(GenericInstanceType gType, out int tupleCardinality)
		{
			tupleCardinality = 0;
			if (gType == null || !gType.Name.StartsWith("ValueTuple`", StringComparison.Ordinal) || gType.Namespace != "System")
				return false;
			if (gType.GenericArguments.Count == TupleType.RestPosition) {
				if (IsValueTuple(gType.GenericArguments.Last() as GenericInstanceType, out tupleCardinality)) {
					tupleCardinality += TupleType.RestPosition - 1;
					return true;
				}
			}
			tupleCardinality = gType.GenericArguments.Count;
			return tupleCardinality > 0 && tupleCardinality < TupleType.RestPosition;
		}

		static ImmutableArray<string> GetTupleElementNames(ICustomAttributeProvider attributeProvider, int tupleTypeIndex, int tupleCardinality)
		{
			if (attributeProvider == null || !attributeProvider.HasCustomAttributes)
				return default(ImmutableArray<string>);
			foreach (CustomAttribute a in attributeProvider.CustomAttributes) {
				TypeReference type = a.AttributeType;
				if (type.Name == "TupleElementNamesAttribute" && type.Namespace == "System.Runtime.CompilerServices") {
					if (a.ConstructorArguments.Count == 1) {
						CustomAttributeArgument[] values = a.ConstructorArguments[0].Value as CustomAttributeArgument[];
						if (values != null) {
							string[] extractedValues = new string[tupleCardinality];
							for (int i = 0; i < tupleCardinality; i++) {
								if (tupleTypeIndex + i < values.Length) {
									extractedValues[i] = values[tupleTypeIndex + i].Value as string;
								}
							}
							return extractedValues.ToImmutableArray();
						}
					}
				}
			}
			return default(ImmutableArray<string>);
		}

		IAssemblyReference GetAssemblyReference(IMetadataScope scope)
		{
			if (scope == null || scope == currentModule)
				return DefaultAssemblyReference.CurrentAssembly;
			else
				return interningProvider.Intern(new DefaultAssemblyReference(scope.Name));
		}
		
		static bool HasDynamicAttribute(ICustomAttributeProvider attributeProvider, int typeIndex)
		{
			if (attributeProvider == null || !attributeProvider.HasCustomAttributes)
				return false;
			foreach (CustomAttribute a in attributeProvider.CustomAttributes) {
				TypeReference type = a.AttributeType;
				if (type.Name == "DynamicAttribute" && type.Namespace == "System.Runtime.CompilerServices") {
					if (a.ConstructorArguments.Count == 1) {
						CustomAttributeArgument[] values = a.ConstructorArguments[0].Value as CustomAttributeArgument[];
						if (values != null && typeIndex < values.Length && values[typeIndex].Value is bool)
							return (bool)values[typeIndex].Value;
					}
					return true;
				}
			}
			return false;
		}

		sealed class TypeDefTokenTypeReference : ITypeReference
		{
			readonly MetadataToken token;

			public TypeDefTokenTypeReference(MetadataToken token)
			{
				if (token.TokenType != TokenType.TypeDef)
					throw new ArgumentException(nameof(token), "must be TypeDef token");
				this.token = token;
			}

			public IType Resolve(ITypeResolveContext context)
			{
				ITypeDefinition td = context.CurrentAssembly.ResolveTypeDefToken(token);
				if (td != null)
					return td;
				return SpecialType.UnknownType;
			}
		}
		#endregion

		#region Read Attributes
		#region Assembly Attributes
		static readonly ITypeReference assemblyVersionAttributeTypeRef = typeof(System.Reflection.AssemblyVersionAttribute).ToTypeReference();
		
		void AddAttributes(AssemblyDefinition assembly, IList<IUnresolvedAttribute> outputList)
		{
			if (assembly.HasCustomAttributes) {
				AddCustomAttributes(assembly.CustomAttributes, outputList);
			}
			if (assembly.HasSecurityDeclarations) {
				AddSecurityAttributes(assembly.SecurityDeclarations, outputList);
			}
			
			// AssemblyVersionAttribute
			if (assembly.Name.Version != null) {
				var assemblyVersion = new DefaultUnresolvedAttribute(assemblyVersionAttributeTypeRef, new[] { KnownTypeReference.String });
				assemblyVersion.PositionalArguments.Add(CreateSimpleConstantValue(KnownTypeReference.String, assembly.Name.Version.ToString()));
				outputList.Add(interningProvider.Intern(assemblyVersion));
			}
		}
		
		IConstantValue CreateSimpleConstantValue(ITypeReference type, object value)
		{
			return interningProvider.Intern(new SimpleConstantValue(type, interningProvider.InternValue(value)));
		}
		#endregion
		
		#region Module Attributes
		void AddAttributes(ModuleDefinition module, IList<IUnresolvedAttribute> outputList)
		{
			if (module.HasCustomAttributes) {
				AddCustomAttributes(module.CustomAttributes, outputList);
			}
		}
		#endregion
		
		#region Parameter Attributes
		static readonly IUnresolvedAttribute inAttribute = new DefaultUnresolvedAttribute(typeof(InAttribute).ToTypeReference());
		static readonly IUnresolvedAttribute outAttribute = new DefaultUnresolvedAttribute(typeof(OutAttribute).ToTypeReference());
		
		void AddAttributes(ParameterDefinition parameter, DefaultUnresolvedParameter targetParameter)
		{
			if (!targetParameter.IsOut) {
				if (parameter.IsIn)
					targetParameter.Attributes.Add(inAttribute);
				if (parameter.IsOut)
					targetParameter.Attributes.Add(outAttribute);
			}
			if (parameter.HasCustomAttributes) {
				AddCustomAttributes(parameter.CustomAttributes, targetParameter.Attributes);
			}
			if (parameter.HasMarshalInfo) {
				targetParameter.Attributes.Add(ConvertMarshalInfo(parameter.MarshalInfo));
			}
		}
		#endregion
		
		#region Method Attributes
		static readonly ITypeReference dllImportAttributeTypeRef = typeof(DllImportAttribute).ToTypeReference();
		static readonly SimpleConstantValue trueValue = new SimpleConstantValue(KnownTypeReference.Boolean, true);
		static readonly SimpleConstantValue falseValue = new SimpleConstantValue(KnownTypeReference.Boolean, false);
		static readonly ITypeReference callingConventionTypeRef = typeof(CallingConvention).ToTypeReference();
		static readonly IUnresolvedAttribute preserveSigAttribute = new DefaultUnresolvedAttribute(typeof(PreserveSigAttribute).ToTypeReference());
		static readonly ITypeReference methodImplAttributeTypeRef = typeof(MethodImplAttribute).ToTypeReference();
		static readonly ITypeReference methodImplOptionsTypeRef = typeof(MethodImplOptions).ToTypeReference();
		
		static bool HasAnyAttributes(MethodDefinition methodDefinition)
		{
			if (methodDefinition.HasPInvokeInfo)
				return true;
			if ((methodDefinition.ImplAttributes & ~MethodImplAttributes.CodeTypeMask) != 0)
				return true;
			if (methodDefinition.MethodReturnType.HasFieldMarshal)
				return true;
			return methodDefinition.HasCustomAttributes || methodDefinition.MethodReturnType.HasCustomAttributes;
		}
		
		void AddAttributes(MethodDefinition methodDefinition, IList<IUnresolvedAttribute> attributes, IList<IUnresolvedAttribute> returnTypeAttributes)
		{
			MethodImplAttributes implAttributes = methodDefinition.ImplAttributes & ~MethodImplAttributes.CodeTypeMask;
			
			#region DllImportAttribute
			if (methodDefinition.HasPInvokeInfo && methodDefinition.PInvokeInfo != null) {
				PInvokeInfo info = methodDefinition.PInvokeInfo;
				var dllImport = new DefaultUnresolvedAttribute(dllImportAttributeTypeRef, new[] { KnownTypeReference.String });
				dllImport.PositionalArguments.Add(CreateSimpleConstantValue(KnownTypeReference.String, info.Module.Name));
				
				if (info.IsBestFitDisabled)
					dllImport.AddNamedFieldArgument("BestFitMapping", falseValue);
				if (info.IsBestFitEnabled)
					dllImport.AddNamedFieldArgument("BestFitMapping", trueValue);
				
				CallingConvention callingConvention;
				switch (info.Attributes & PInvokeAttributes.CallConvMask) {
					case (PInvokeAttributes)0:
						Debug.WriteLine ("P/Invoke calling convention not set on:" + methodDefinition.FullName);
						callingConvention = 0;
						break;
					case PInvokeAttributes.CallConvCdecl:
						callingConvention = CallingConvention.Cdecl;
						break;
					case PInvokeAttributes.CallConvFastcall:
						callingConvention = CallingConvention.FastCall;
						break;
					case PInvokeAttributes.CallConvStdCall:
						callingConvention = CallingConvention.StdCall;
						break;
					case PInvokeAttributes.CallConvThiscall:
						callingConvention = CallingConvention.ThisCall;
						break;
					case PInvokeAttributes.CallConvWinapi:
						callingConvention = CallingConvention.Winapi;
						break;
					default:
						throw new NotSupportedException("unknown calling convention");
				}
				if (callingConvention != CallingConvention.Winapi)
					dllImport.AddNamedFieldArgument("CallingConvention", CreateSimpleConstantValue(callingConventionTypeRef, (int)callingConvention));
				
				CharSet charSet = CharSet.None;
				switch (info.Attributes & PInvokeAttributes.CharSetMask) {
					case PInvokeAttributes.CharSetAnsi:
						charSet = CharSet.Ansi;
						break;
					case PInvokeAttributes.CharSetAuto:
						charSet = CharSet.Auto;
						break;
					case PInvokeAttributes.CharSetUnicode:
						charSet = CharSet.Unicode;
						break;
				}
				if (charSet != CharSet.None)
					dllImport.AddNamedFieldArgument("CharSet", CreateSimpleConstantValue(charSetTypeRef, (int)charSet));
				
				if (!string.IsNullOrEmpty(info.EntryPoint) && info.EntryPoint != methodDefinition.Name)
					dllImport.AddNamedFieldArgument("EntryPoint", CreateSimpleConstantValue(KnownTypeReference.String, info.EntryPoint));
				
				if (info.IsNoMangle)
					dllImport.AddNamedFieldArgument("ExactSpelling", trueValue);
				
				if ((implAttributes & MethodImplAttributes.PreserveSig) == MethodImplAttributes.PreserveSig)
					implAttributes &= ~MethodImplAttributes.PreserveSig;
				else
					dllImport.AddNamedFieldArgument("PreserveSig", falseValue);
				
				if (info.SupportsLastError)
					dllImport.AddNamedFieldArgument("SetLastError", trueValue);
				
				if (info.IsThrowOnUnmappableCharDisabled)
					dllImport.AddNamedFieldArgument("ThrowOnUnmappableChar", falseValue);
				if (info.IsThrowOnUnmappableCharEnabled)
					dllImport.AddNamedFieldArgument("ThrowOnUnmappableChar", trueValue);
				
				attributes.Add(interningProvider.Intern(dllImport));
			}
			#endregion
			
			#region PreserveSigAttribute
			if (implAttributes == MethodImplAttributes.PreserveSig) {
				attributes.Add(preserveSigAttribute);
				implAttributes = 0;
			}
			#endregion
			
			#region MethodImplAttribute
			if (implAttributes != 0) {
				var methodImpl = new DefaultUnresolvedAttribute(methodImplAttributeTypeRef, new[] { methodImplOptionsTypeRef });
				methodImpl.PositionalArguments.Add(CreateSimpleConstantValue(methodImplOptionsTypeRef, (int)implAttributes));
				attributes.Add(interningProvider.Intern(methodImpl));
			}
			#endregion
			
			if (methodDefinition.HasCustomAttributes) {
				AddCustomAttributes(methodDefinition.CustomAttributes, attributes);
			}
			if (methodDefinition.HasSecurityDeclarations) {
				AddSecurityAttributes(methodDefinition.SecurityDeclarations, attributes);
			}
			if (methodDefinition.MethodReturnType.HasMarshalInfo) {
				returnTypeAttributes.Add(ConvertMarshalInfo(methodDefinition.MethodReturnType.MarshalInfo));
			}
			if (methodDefinition.MethodReturnType.HasCustomAttributes) {
				AddCustomAttributes(methodDefinition.MethodReturnType.CustomAttributes, returnTypeAttributes);
			}
		}
		#endregion
		
		#region Type Attributes
		static readonly DefaultUnresolvedAttribute serializableAttribute = new DefaultUnresolvedAttribute(typeof(SerializableAttribute).ToTypeReference());
		static readonly DefaultUnresolvedAttribute comImportAttribute = new DefaultUnresolvedAttribute(typeof(ComImportAttribute).ToTypeReference());
		static readonly ITypeReference structLayoutAttributeTypeRef = typeof(StructLayoutAttribute).ToTypeReference();
		static readonly ITypeReference layoutKindTypeRef = typeof(LayoutKind).ToTypeReference();
		static readonly ITypeReference charSetTypeRef = typeof(CharSet).ToTypeReference();
		
		void AddAttributes(TypeDefinition typeDefinition, IUnresolvedTypeDefinition targetEntity)
		{
			// SerializableAttribute
			if (typeDefinition.IsSerializable)
				targetEntity.Attributes.Add(serializableAttribute);
			
			// ComImportAttribute
			if (typeDefinition.IsImport)
				targetEntity.Attributes.Add(comImportAttribute);
			
			#region StructLayoutAttribute
			LayoutKind layoutKind = LayoutKind.Auto;
			switch (typeDefinition.Attributes & TypeAttributes.LayoutMask) {
				case TypeAttributes.SequentialLayout:
					layoutKind = LayoutKind.Sequential;
					break;
				case TypeAttributes.ExplicitLayout:
					layoutKind = LayoutKind.Explicit;
					break;
			}
			CharSet charSet = CharSet.None;
			switch (typeDefinition.Attributes & TypeAttributes.StringFormatMask) {
				case TypeAttributes.AnsiClass:
					charSet = CharSet.Ansi;
					break;
				case TypeAttributes.AutoClass:
					charSet = CharSet.Auto;
					break;
				case TypeAttributes.UnicodeClass:
					charSet = CharSet.Unicode;
					break;
			}
			LayoutKind defaultLayoutKind = (typeDefinition.IsValueType && !typeDefinition.IsEnum) ? LayoutKind.Sequential: LayoutKind.Auto;
			if (layoutKind != defaultLayoutKind || charSet != CharSet.Ansi || typeDefinition.PackingSize > 0 || typeDefinition.ClassSize > 0) {
				DefaultUnresolvedAttribute structLayout = new DefaultUnresolvedAttribute(structLayoutAttributeTypeRef, new[] { layoutKindTypeRef });
				structLayout.PositionalArguments.Add(CreateSimpleConstantValue(layoutKindTypeRef, (int)layoutKind));
				if (charSet != CharSet.Ansi) {
					structLayout.AddNamedFieldArgument("CharSet", CreateSimpleConstantValue(charSetTypeRef, (int)charSet));
				}
				if (typeDefinition.PackingSize > 0) {
					structLayout.AddNamedFieldArgument("Pack", CreateSimpleConstantValue(KnownTypeReference.Int32, (int)typeDefinition.PackingSize));
				}
				if (typeDefinition.ClassSize > 0) {
					structLayout.AddNamedFieldArgument("Size", CreateSimpleConstantValue(KnownTypeReference.Int32, (int)typeDefinition.ClassSize));
				}
				targetEntity.Attributes.Add(interningProvider.Intern(structLayout));
			}
			#endregion
			
			if (typeDefinition.HasCustomAttributes) {
				AddCustomAttributes(typeDefinition.CustomAttributes, targetEntity.Attributes);
			}
			if (typeDefinition.HasSecurityDeclarations) {
				AddSecurityAttributes(typeDefinition.SecurityDeclarations, targetEntity.Attributes);
			}
		}
		#endregion
		
		#region Field Attributes
		static readonly ITypeReference fieldOffsetAttributeTypeRef = typeof(FieldOffsetAttribute).ToTypeReference();
		static readonly IUnresolvedAttribute nonSerializedAttribute = new DefaultUnresolvedAttribute(typeof(NonSerializedAttribute).ToTypeReference());
		
		void AddAttributes(FieldDefinition fieldDefinition, IUnresolvedEntity targetEntity)
		{
			// FieldOffsetAttribute
			if (fieldDefinition.HasLayoutInfo) {
				DefaultUnresolvedAttribute fieldOffset = new DefaultUnresolvedAttribute(fieldOffsetAttributeTypeRef, new[] { KnownTypeReference.Int32 });
				fieldOffset.PositionalArguments.Add(CreateSimpleConstantValue(KnownTypeReference.Int32, fieldDefinition.Offset));
				targetEntity.Attributes.Add(interningProvider.Intern(fieldOffset));
			}
			
			// NonSerializedAttribute
			if (fieldDefinition.IsNotSerialized) {
				targetEntity.Attributes.Add(nonSerializedAttribute);
			}
			
			if (fieldDefinition.HasMarshalInfo) {
				targetEntity.Attributes.Add(ConvertMarshalInfo(fieldDefinition.MarshalInfo));
			}
			
			if (fieldDefinition.HasCustomAttributes) {
				AddCustomAttributes(fieldDefinition.CustomAttributes, targetEntity.Attributes);
			}
		}
		#endregion
		
		#region Event Attributes
		void AddAttributes(EventDefinition eventDefinition, IUnresolvedEntity targetEntity)
		{
			if (eventDefinition.HasCustomAttributes) {
				AddCustomAttributes(eventDefinition.CustomAttributes, targetEntity.Attributes);
			}
		}
		#endregion
		
		#region Property Attributes
		void AddAttributes(PropertyDefinition propertyDefinition, IUnresolvedEntity targetEntity)
		{
			if (propertyDefinition.HasCustomAttributes) {
				AddCustomAttributes(propertyDefinition.CustomAttributes, targetEntity.Attributes);
			}
		}
		#endregion
		
		#region Type Parameter Attributes
		void AddAttributes(GenericParameter genericParameter, IUnresolvedTypeParameter targetTP)
		{
			if (genericParameter.HasCustomAttributes) {
				AddCustomAttributes(genericParameter.CustomAttributes, targetTP.Attributes);
			}
		}
		#endregion
		
		#region MarshalAsAttribute (ConvertMarshalInfo)
		static readonly ITypeReference marshalAsAttributeTypeRef = typeof(MarshalAsAttribute).ToTypeReference();
		static readonly ITypeReference unmanagedTypeTypeRef = typeof(UnmanagedType).ToTypeReference();
		
		IUnresolvedAttribute ConvertMarshalInfo(MarshalInfo marshalInfo)
		{
			DefaultUnresolvedAttribute attr = new DefaultUnresolvedAttribute(marshalAsAttributeTypeRef, new[] { unmanagedTypeTypeRef });
			attr.PositionalArguments.Add(CreateSimpleConstantValue(unmanagedTypeTypeRef, (int)marshalInfo.NativeType));
			
			FixedArrayMarshalInfo fami = marshalInfo as FixedArrayMarshalInfo;
			if (fami != null) {
				attr.AddNamedFieldArgument("SizeConst", CreateSimpleConstantValue(KnownTypeReference.Int32, (int)fami.Size));
				if (fami.ElementType != NativeType.None)
					attr.AddNamedFieldArgument("ArraySubType", CreateSimpleConstantValue(unmanagedTypeTypeRef, (int)fami.ElementType));
			}
			SafeArrayMarshalInfo sami = marshalInfo as SafeArrayMarshalInfo;
			if (sami != null && sami.ElementType != VariantType.None) {
				attr.AddNamedFieldArgument("SafeArraySubType", CreateSimpleConstantValue(typeof(VarEnum).ToTypeReference(), (int)sami.ElementType));
			}
			ArrayMarshalInfo ami = marshalInfo as ArrayMarshalInfo;
			if (ami != null) {
				if (ami.ElementType != NativeType.Max)
					attr.AddNamedFieldArgument("ArraySubType", CreateSimpleConstantValue(unmanagedTypeTypeRef, (int)ami.ElementType));
				if (ami.Size >= 0)
					attr.AddNamedFieldArgument("SizeConst", CreateSimpleConstantValue(KnownTypeReference.Int32, (int)ami.Size));
				if (ami.SizeParameterMultiplier != 0 && ami.SizeParameterIndex >= 0)
					attr.AddNamedFieldArgument("SizeParamIndex", CreateSimpleConstantValue(KnownTypeReference.Int16, (short)ami.SizeParameterIndex));
			}
			CustomMarshalInfo cmi = marshalInfo as CustomMarshalInfo;
			if (cmi != null) {
				if (cmi.ManagedType != null)
					attr.AddNamedFieldArgument("MarshalType", CreateSimpleConstantValue(KnownTypeReference.String, cmi.ManagedType.FullName));
				if (!string.IsNullOrEmpty(cmi.Cookie))
					attr.AddNamedFieldArgument("MarshalCookie", CreateSimpleConstantValue(KnownTypeReference.String, cmi.Cookie));
			}
			FixedSysStringMarshalInfo fssmi = marshalInfo as FixedSysStringMarshalInfo;
			if (fssmi != null) {
				attr.AddNamedFieldArgument("SizeConst", CreateSimpleConstantValue(KnownTypeReference.Int32, (int)fssmi.Size));
			}
			
			return InterningProvider.Intern(attr);
		}
		#endregion
		
		#region Custom Attributes (ReadAttribute)
		void AddCustomAttributes(Mono.Collections.Generic.Collection<CustomAttribute> attributes, IList<IUnresolvedAttribute> targetCollection)
		{
			foreach (var cecilAttribute in attributes) {
				TypeReference type = cecilAttribute.AttributeType;
				if (type.Namespace == "System.Runtime.CompilerServices") {
					if (type.Name == "ExtensionAttribute" || type.Name == "DecimalConstantAttribute")
						continue;
					if (UseDynamicType && type.Name == "DynamicAttribute")
						continue;
					if (UseTupleTypes && type.Name == "TupleElementNamesAttribute")
						continue;
				} else if (type.Name == "ParamArrayAttribute" && type.Namespace == "System") {
					continue;
				}
				targetCollection.Add(ReadAttribute(cecilAttribute));
			}
		}
		
		public IUnresolvedAttribute ReadAttribute(CustomAttribute attribute)
		{
			if (attribute == null)
				throw new ArgumentNullException("attribute");
			MethodReference ctor = attribute.Constructor;
			ITypeReference attributeType = ReadTypeReference(attribute.AttributeType);
			IList<ITypeReference> ctorParameterTypes = EmptyList<ITypeReference>.Instance;
			if (ctor.HasParameters) {
				ctorParameterTypes = new ITypeReference[ctor.Parameters.Count];
				for (int i = 0; i < ctorParameterTypes.Count; i++) {
					ctorParameterTypes[i] = ReadTypeReference(ctor.Parameters[i].ParameterType);
				}
				ctorParameterTypes = interningProvider.InternList(ctorParameterTypes);
			}
			return interningProvider.Intern(new UnresolvedAttributeBlob(attributeType, ctorParameterTypes, attribute.GetBlob()));
		}
		#endregion
		
		#region Security Attributes
		/// <summary>
		/// Reads a security declaration.
		/// </summary>
		public IList<IUnresolvedAttribute> ReadSecurityDeclaration(SecurityDeclaration secDecl)
		{
			if (secDecl == null)
				throw new ArgumentNullException("secDecl");
			var result = new List<IUnresolvedAttribute>();
			AddSecurityAttributes(secDecl, result);
			return result;
		}
		
		void AddSecurityAttributes(Mono.Collections.Generic.Collection<SecurityDeclaration> securityDeclarations, IList<IUnresolvedAttribute> targetCollection)
		{
			foreach (var secDecl in securityDeclarations) {
				AddSecurityAttributes(secDecl, targetCollection);
			}
		}
		
		void AddSecurityAttributes(SecurityDeclaration secDecl, IList<IUnresolvedAttribute> targetCollection)
		{
			byte[] blob;
			try {
				blob = secDecl.GetBlob();
			} catch (NotSupportedException) {
				return; // https://github.com/icsharpcode/SharpDevelop/issues/284
			}
			var blobSecDecl = new UnresolvedSecurityDeclarationBlob((int)secDecl.Action, blob);
			targetCollection.AddRange(blobSecDecl.UnresolvedAttributes);
		}
		#endregion
		#endregion
		
		#region Read Type Definition
		DefaultUnresolvedTypeDefinition CreateTopLevelTypeDefinition(TypeDefinition typeDefinition)
		{
			string name = ReflectionHelper.SplitTypeParameterCountFromReflectionName(typeDefinition.Name);
			var td = new DefaultUnresolvedTypeDefinition(typeDefinition.Namespace, name);
			td.MetadataToken = typeDefinition.MetadataToken;
			if (typeDefinition.HasGenericParameters)
				InitTypeParameters(typeDefinition, td.TypeParameters);
			return td;
		}
		
		static void InitTypeParameters(TypeDefinition typeDefinition, IList<IUnresolvedTypeParameter> typeParameters)
		{
			// Type parameters are initialized within the constructor so that the class can be put into the type storage
			// before the rest of the initialization runs - this allows it to be available for early binding as soon as possible.
			for (int i = 0; i < typeDefinition.GenericParameters.Count; i++) {
				if (typeDefinition.GenericParameters[i].Position != i)
					throw new InvalidOperationException("g.Position != i");
				typeParameters.Add(new DefaultUnresolvedTypeParameter(
					SymbolKind.TypeDefinition, i, typeDefinition.GenericParameters[i].Name));
			}
		}
		
		void InitTypeParameterConstraints(TypeDefinition typeDefinition, IList<IUnresolvedTypeParameter> typeParameters)
		{
			for (int i = 0; i < typeParameters.Count; i++) {
				var tp = (DefaultUnresolvedTypeParameter)typeParameters[i];
				AddConstraints(tp, typeDefinition.GenericParameters[i]);
				AddAttributes(typeDefinition.GenericParameters[i], tp);
				tp.ApplyInterningProvider(interningProvider);
			}
		}
		
		void InitTypeDefinition(TypeDefinition typeDefinition, DefaultUnresolvedTypeDefinition td)
		{
			td.Kind = GetTypeKind(typeDefinition);
			InitTypeModifiers(typeDefinition, td);
			InitTypeParameterConstraints(typeDefinition, td.TypeParameters);
			
			// nested types can be initialized only after generic parameters were created
			InitNestedTypes(typeDefinition, td, td.NestedTypes);
			AddAttributes(typeDefinition, td);
			td.HasExtensionMethods = HasExtensionAttribute(typeDefinition);
			
			InitBaseTypes(typeDefinition, td.BaseTypes);
			
			td.AddDefaultConstructorIfRequired = (td.Kind == TypeKind.Struct || td.Kind == TypeKind.Enum);
			InitMembers(typeDefinition, td, td.Members);
			td.ApplyInterningProvider(interningProvider);
			td.Freeze();
			RegisterCecilObject(td, typeDefinition);
		}
		
		void InitBaseTypes(TypeDefinition typeDefinition, IList<ITypeReference> baseTypes)
		{
			// set base classes
			if (typeDefinition.IsEnum) {
				foreach (FieldDefinition enumField in typeDefinition.Fields) {
					if (!enumField.IsStatic) {
						baseTypes.Add(ReadTypeReference(enumField.FieldType));
						break;
					}
				}
			} else {
				if (typeDefinition.BaseType != null) {
					baseTypes.Add(ReadTypeReference(typeDefinition.BaseType));
				}
				if (typeDefinition.HasInterfaces) {
					foreach (var iface in typeDefinition.Interfaces) {
						baseTypes.Add(ReadTypeReference(iface.InterfaceType));
					}
				}
			}
		}
		
		void InitNestedTypes(TypeDefinition typeDefinition, IUnresolvedTypeDefinition declaringTypeDefinition, IList<IUnresolvedTypeDefinition> nestedTypes)
		{
			if (!typeDefinition.HasNestedTypes)
				return;
			foreach (TypeDefinition nestedTypeDef in typeDefinition.NestedTypes) {
				TypeAttributes visibility = nestedTypeDef.Attributes & TypeAttributes.VisibilityMask;
				if (this.IncludeInternalMembers
				    || visibility == TypeAttributes.NestedPublic
				    || visibility == TypeAttributes.NestedFamily
				    || visibility == TypeAttributes.NestedFamORAssem)
				{
					string name = nestedTypeDef.Name;
					int pos = name.LastIndexOf('/');
					if (pos > 0)
						name = name.Substring(pos + 1);
					if (LazyLoad) {
						var nestedTd = new LazyCecilTypeDefinition(this, nestedTypeDef, declaringTypeDefinition, name);
						nestedTypes.Add(nestedTd);
						RegisterCecilObject(nestedTd, nestedTypeDef);
					} else {
						name = ReflectionHelper.SplitTypeParameterCountFromReflectionName(name);
						var nestedType = new DefaultUnresolvedTypeDefinition(declaringTypeDefinition, name);
						nestedType.MetadataToken = nestedTypeDef.MetadataToken;
						InitTypeParameters(nestedTypeDef, nestedType.TypeParameters);
						nestedTypes.Add(nestedType);
						InitTypeDefinition(nestedTypeDef, nestedType);
					}
				}
			}
		}
		
		static TypeKind GetTypeKind(TypeDefinition typeDefinition)
		{
			// set classtype
			if (typeDefinition.IsInterface) {
				return TypeKind.Interface;
			} else if (typeDefinition.IsEnum) {
				return TypeKind.Enum;
			} else if (typeDefinition.IsValueType) {
				return TypeKind.Struct;
			} else if (IsDelegate(typeDefinition)) {
				return TypeKind.Delegate;
			} else if (IsModule(typeDefinition)) {
				return TypeKind.Module;
			} else {
				return TypeKind.Class;
			}
		}
		
		static void InitTypeModifiers(TypeDefinition typeDefinition, AbstractUnresolvedEntity td)
		{
			td.IsSealed = typeDefinition.IsSealed;
			td.IsAbstract = typeDefinition.IsAbstract;
			switch (typeDefinition.Attributes & TypeAttributes.VisibilityMask) {
				case TypeAttributes.NotPublic:
				case TypeAttributes.NestedAssembly:
					td.Accessibility = Accessibility.Internal;
					break;
				case TypeAttributes.Public:
				case TypeAttributes.NestedPublic:
					td.Accessibility = Accessibility.Public;
					break;
				case TypeAttributes.NestedPrivate:
					td.Accessibility = Accessibility.Private;
					break;
				case TypeAttributes.NestedFamily:
					td.Accessibility = Accessibility.Protected;
					break;
				case TypeAttributes.NestedFamANDAssem:
					td.Accessibility = Accessibility.ProtectedAndInternal;
					break;
				case TypeAttributes.NestedFamORAssem:
					td.Accessibility = Accessibility.ProtectedOrInternal;
					break;
			}
		}
		
		static bool IsDelegate(TypeDefinition type)
		{
			if (type.BaseType != null && type.BaseType.Namespace == "System") {
				if (type.BaseType.Name == "MulticastDelegate")
					return true;
				if (type.BaseType.Name == "Delegate" && type.Name != "MulticastDelegate")
					return true;
			}
			return false;
		}
		
		static bool IsModule(TypeDefinition type)
		{
			if (!type.HasCustomAttributes)
				return false;
			foreach (var att in type.CustomAttributes) {
				if (att.AttributeType.FullName == "Microsoft.VisualBasic.CompilerServices.StandardModuleAttribute"
				    || att.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGlobalScopeAttribute")
				{
					return true;
				}
			}
			return false;
		}
		
		void InitMembers(TypeDefinition typeDefinition, IUnresolvedTypeDefinition td, IList<IUnresolvedMember> members)
		{
			if (typeDefinition.HasMethods) {
				foreach (MethodDefinition method in typeDefinition.Methods) {
					if (IsVisible(method.Attributes) && !IsAccessor(method.SemanticsAttributes)) {
						SymbolKind type = SymbolKind.Method;
						if (method.IsSpecialName) {
							if (method.IsConstructor)
								type = SymbolKind.Constructor;
							else if (method.Name.StartsWith("op_", StringComparison.Ordinal))
								type = SymbolKind.Operator;
						}
						members.Add(ReadMethod(method, td, type));
					}
				}
			}
			if (typeDefinition.HasFields) {
				foreach (FieldDefinition field in typeDefinition.Fields) {
					if (IsVisible(field.Attributes) && !field.IsSpecialName) {
						members.Add(ReadField(field, td));
					}
				}
			}
			if (typeDefinition.HasProperties) {
				string defaultMemberName = null;
				var defaultMemberAttribute = typeDefinition.CustomAttributes.FirstOrDefault(
					a => a.AttributeType.FullName == typeof(System.Reflection.DefaultMemberAttribute).FullName);
				if (defaultMemberAttribute != null && defaultMemberAttribute.ConstructorArguments.Count == 1) {
					defaultMemberName = defaultMemberAttribute.ConstructorArguments[0].Value as string;
				}
				foreach (PropertyDefinition property in typeDefinition.Properties) {
					bool getterVisible = property.GetMethod != null && IsVisible(property.GetMethod.Attributes);
					bool setterVisible = property.SetMethod != null && IsVisible(property.SetMethod.Attributes);
					if (getterVisible || setterVisible) {
						SymbolKind type = SymbolKind.Property;
						if (property.HasParameters) {
							// Try to detect indexer:
							if (property.Name == defaultMemberName) {
								type = SymbolKind.Indexer; // normal indexer
							} else if (property.Name.EndsWith(".Item", StringComparison.Ordinal) && (property.GetMethod ?? property.SetMethod).HasOverrides) {
								// explicit interface implementation of indexer
								type = SymbolKind.Indexer;
								// We can't really tell parameterized properties and indexers apart in this case without
								// resolving the interface, so we rely on the "Item" naming convention instead.
							}
						}
						members.Add(ReadProperty(property, td, type));
					}
				}
			}
			if (typeDefinition.HasEvents) {
				foreach (EventDefinition ev in typeDefinition.Events) {
					if (ev.AddMethod != null && IsVisible(ev.AddMethod.Attributes)) {
						members.Add(ReadEvent(ev, td));
					}
				}
			}
		}
		
		static bool IsAccessor(MethodSemanticsAttributes semantics)
		{
			return !(semantics == MethodSemanticsAttributes.None || semantics == MethodSemanticsAttributes.Other);
		}
		#endregion
		
		#region Lazy-Loaded Type Definition
		/// <summary>
		/// Given an assembly that was created by the CecilLoader with lazy-loading enabled,
		/// this method will eagerly load all classes, and free any references to the source Cecil objects.
		/// 
		/// The intended usage pattern for this method is:
		/// 1. use lazy-loading to improve the latency when new assemblies have to be loaded
		/// 2. later, when the CPU is idle, call FinishLazyLoading() to free up the memory consumed by the Cecil objects
		/// </summary>
		public static void FinishLazyLoading(IUnresolvedAssembly assembly)
		{
			if (assembly == null)
				throw new ArgumentNullException("assembly");
			foreach (var type in assembly.TopLevelTypeDefinitions) {
				var lctd = type as LazyCecilTypeDefinition;
				if (lctd != null)
					lctd.InitAndReleaseReferences();
			}
		}
		
		sealed class LazyCecilTypeDefinition : AbstractUnresolvedEntity, IUnresolvedTypeDefinition
		{
			// loader + cecilTypeDef, used for lazy-loading; and set to null after lazy loading is complete
			CecilLoader loader;
			TypeDefinition cecilTypeDef;
			
			readonly string namespaceName;
			readonly TypeKind kind;
			readonly IList<IUnresolvedTypeParameter> typeParameters;
			
			// lazy-loaded fields
			IList<ITypeReference> baseTypes;
			IList<IUnresolvedTypeDefinition> nestedTypes;
			IList<IUnresolvedMember> members;
			
			public LazyCecilTypeDefinition(CecilLoader loader, TypeDefinition typeDefinition, IUnresolvedTypeDefinition declaringTypeDefinition = null, string name = null)
			{
				this.loader = loader;
				this.cecilTypeDef = typeDefinition;
				this.MetadataToken = typeDefinition.MetadataToken;
				this.SymbolKind = SymbolKind.TypeDefinition;
				if (declaringTypeDefinition != null) {
					this.DeclaringTypeDefinition = declaringTypeDefinition;
					this.namespaceName = declaringTypeDefinition.Namespace;
				} else {
					this.namespaceName = typeDefinition.Namespace;
				}
				this.Name = ReflectionHelper.SplitTypeParameterCountFromReflectionName(name ?? typeDefinition.Name);
				var tps = new List<IUnresolvedTypeParameter>();
				InitTypeParameters(typeDefinition, tps);
				this.typeParameters = FreezableHelper.FreezeList(tps);
				
				this.kind = GetTypeKind(typeDefinition);
				InitTypeModifiers(typeDefinition, this);
				loader.InitTypeParameterConstraints(typeDefinition, typeParameters);
				
				loader.AddAttributes(typeDefinition, this);
				flags[FlagHasExtensionMethods] = HasExtensionAttribute(typeDefinition);
				
				this.ApplyInterningProvider(loader.interningProvider);
				this.Freeze();
			}
			
			public override string Namespace {
				get { return namespaceName; }
				set { throw new NotSupportedException(); }
			}
			
			public override string ReflectionName {
				get { return this.FullTypeName.ReflectionName; }
			}
			
			public FullTypeName FullTypeName {
				get {
					IUnresolvedTypeDefinition declaringTypeDef = this.DeclaringTypeDefinition;
					if (declaringTypeDef != null) {
						return declaringTypeDef.FullTypeName.NestedType(this.Name, typeParameters.Count - declaringTypeDef.TypeParameters.Count);
					} else {
						return new TopLevelTypeName(namespaceName, this.Name, typeParameters.Count);
					}
				}
			}
			
			public TypeKind Kind {
				get { return kind; }
			}
			
			public IList<IUnresolvedTypeParameter> TypeParameters {
				get { return typeParameters; }
			}
			
			public IList<ITypeReference> BaseTypes {
				get {
					var result = LazyInit.VolatileRead(ref this.baseTypes);
					if (result != null) {
						return result;
					} else {
						return LazyInit.GetOrSet(ref this.baseTypes, TryInitBaseTypes());
					}
				}
			}
			
			IList<ITypeReference> TryInitBaseTypes()
			{
				var loader = LazyInit.VolatileRead(ref this.loader);
				var cecilTypeDef = LazyInit.VolatileRead(ref this.cecilTypeDef);
				if (loader == null || cecilTypeDef == null) {
					// Cannot initialize because the references to loader/cecilTypeDef
					// have already been cleared.
					// This can only happen if the class was loaded by another thread concurrently to the TryInitBaseTypes() call,
					// so the GetOrSet() call in the property will retrieve the value set by the other thread.
					return null;
				}
				lock (loader.currentModule) {
					var result = new List<ITypeReference>();
					loader.InitBaseTypes(cecilTypeDef, result);
					return FreezableHelper.FreezeList(result);
				}
			}
			
			public IList<IUnresolvedTypeDefinition> NestedTypes {
				get {
					var result = LazyInit.VolatileRead(ref this.nestedTypes);
					if (result != null) {
						return result;
					} else {
						return LazyInit.GetOrSet(ref this.nestedTypes, TryInitNestedTypes());
					}
				}
			}
			
			IList<IUnresolvedTypeDefinition> TryInitNestedTypes()
			{
				var loader = LazyInit.VolatileRead(ref this.loader);
				var cecilTypeDef = LazyInit.VolatileRead(ref this.cecilTypeDef);
				if (loader == null || cecilTypeDef == null) {
					// Cannot initialize because the references to loader/cecilTypeDef
					// have already been cleared.
					// This can only happen if the class was loaded by another thread concurrently to the TryInitNestedTypes() call,
					// so the GetOrSet() call in the property will retrieve the value set by the other thread.
					return null;
				}
				lock (loader.currentModule) {
					var result = new List<IUnresolvedTypeDefinition>();
					loader.InitNestedTypes(cecilTypeDef, this, result);
					return FreezableHelper.FreezeList(result);
				}
			}
			
			public IList<IUnresolvedMember> Members {
				get {
					var result = LazyInit.VolatileRead(ref this.members);
					if (result != null) {
						return result;
					} else {
						return LazyInit.GetOrSet(ref this.members, TryInitMembers());
					}
				}
			}
			
			IList<IUnresolvedMember> TryInitMembers()
			{
				var loader = LazyInit.VolatileRead(ref this.loader);
				var cecilTypeDef = LazyInit.VolatileRead(ref this.cecilTypeDef);
				if (loader == null || cecilTypeDef == null) {
					// Cannot initialize because the references to loader/cecilTypeDef
					// have already been cleared.
					// This can only happen if the class was loaded by another thread concurrently to the TryInitMembers() call,
					// so the GetOrSet() call in the property will retrieve the value set by the other thread.
					return null;
				}
				lock (loader.currentModule) {
					if (this.members != null)
						return this.members;
					var result = new List<IUnresolvedMember>();
					loader.InitMembers(cecilTypeDef, this, result);
					return FreezableHelper.FreezeList(result);
				}
			}
			
			public void InitAndReleaseReferences()
			{
				if (LazyInit.VolatileRead(ref this.baseTypes) == null)
					LazyInit.GetOrSet(ref this.baseTypes, TryInitBaseTypes());
				if (LazyInit.VolatileRead(ref this.nestedTypes) == null)
					LazyInit.GetOrSet(ref this.nestedTypes, TryInitNestedTypes());
				if (LazyInit.VolatileRead(ref this.members) == null)
					LazyInit.GetOrSet(ref this.members, TryInitMembers());
				Thread.MemoryBarrier(); // commit lazily-initialized fields to memory before nulling out the references
				// Allow the GC to collect the cecil type definition
				loader = null;
				cecilTypeDef = null;
			}
			
			public IEnumerable<IUnresolvedMethod> Methods {
				get { return Members.OfType<IUnresolvedMethod>(); }
			}
			
			public IEnumerable<IUnresolvedProperty> Properties {
				get { return Members.OfType<IUnresolvedProperty>(); }
			}
			
			public IEnumerable<IUnresolvedField> Fields {
				get { return Members.OfType<IUnresolvedField>(); }
			}
			
			public IEnumerable<IUnresolvedEvent> Events {
				get { return Members.OfType<IUnresolvedEvent>(); }
			}
			
			public bool AddDefaultConstructorIfRequired {
				get { return kind == TypeKind.Struct || kind == TypeKind.Enum; }
			}
			
			public bool? HasExtensionMethods {
				get { return flags[FlagHasExtensionMethods]; }
				// we always return true or false, never null.
				// FlagHasNoExtensionMethods is unused in LazyCecilTypeDefinition
			}
			
			public bool IsPartial {
				get { return false; }
			}
			
			public override object Clone()
			{
				throw new NotSupportedException();
			}
			
			public IType Resolve(ITypeResolveContext context)
			{
				if (context == null)
					throw new ArgumentNullException("context");
				if (context.CurrentAssembly == null)
					throw new ArgumentException("An ITypeDefinition cannot be resolved in a context without a current assembly.");
				return context.CurrentAssembly.ResolveTypeDefToken(this.MetadataToken)
					?? (IType)new UnknownType(this.Namespace, this.Name, this.TypeParameters.Count);
			}
			
			public ITypeResolveContext CreateResolveContext(ITypeResolveContext parentContext)
			{
				return parentContext;
			}
		}
		#endregion
		
		#region Read Method
		public IUnresolvedMethod ReadMethod(MethodDefinition method, IUnresolvedTypeDefinition parentType, SymbolKind methodType = SymbolKind.Method)
		{
			return ReadMethod(method, parentType, methodType, null);
		}
		
		IUnresolvedMethod ReadMethod(MethodDefinition method, IUnresolvedTypeDefinition parentType, SymbolKind methodType, IUnresolvedMember accessorOwner)
		{
			if (method == null)
				return null;
			DefaultUnresolvedMethod m = new DefaultUnresolvedMethod(parentType, method.Name);
			m.SymbolKind = methodType;
			m.AccessorOwner = accessorOwner;
			m.HasBody = method.HasBody;
			if (method.HasGenericParameters) {
				for (int i = 0; i < method.GenericParameters.Count; i++) {
					if (method.GenericParameters[i].Position != i)
						throw new InvalidOperationException("g.Position != i");
					m.TypeParameters.Add(new DefaultUnresolvedTypeParameter(
						SymbolKind.Method, i, method.GenericParameters[i].Name));
				}
				for (int i = 0; i < method.GenericParameters.Count; i++) {
					var tp = (DefaultUnresolvedTypeParameter)m.TypeParameters[i];
					AddConstraints(tp, method.GenericParameters[i]);
					AddAttributes(method.GenericParameters[i], tp);
					tp.ApplyInterningProvider(interningProvider);
				}
			}
			
			m.ReturnType = ReadTypeReference(method.ReturnType, typeAttributes: method.MethodReturnType, isFromSignature: true);
			
			if (HasAnyAttributes(method))
				AddAttributes(method, m.Attributes, m.ReturnTypeAttributes);
			TranslateModifiers(method, m);
			
			if (method.HasParameters) {
				foreach (ParameterDefinition p in method.Parameters) {
					m.Parameters.Add(ReadParameter(p));
				}
			}
			if (method.CallingConvention == MethodCallingConvention.VarArg) {
				m.Parameters.Add(new DefaultUnresolvedParameter(SpecialType.ArgList, string.Empty));
			}
			
			// mark as extension method if the attribute is set
			if (method.IsStatic && HasExtensionAttribute(method)) {
				m.IsExtensionMethod = true;
			}

			int lastDot = method.Name.LastIndexOf('.');
			if (lastDot >= 0 && method.HasOverrides) {
				// To be consistent with the parser-initialized type system, shorten the method name:
				if (ShortenInterfaceImplNames)
					m.Name = method.Name.Substring(lastDot + 1);
				m.IsExplicitInterfaceImplementation = true;
				foreach (var or in method.Overrides) {
					m.ExplicitInterfaceImplementations.Add(new DefaultMemberReference(
						accessorOwner != null ? SymbolKind.Accessor : SymbolKind.Method,
						ReadTypeReference(or.DeclaringType),
						or.Name, or.GenericParameters.Count, m.Parameters.Select(p => p.Type).ToList()));
				}
			}

			FinishReadMember(m, method);
			return m;
		}
		
		static bool HasExtensionAttribute(ICustomAttributeProvider provider)
		{
			if (provider.HasCustomAttributes) {
				foreach (var attr in provider.CustomAttributes) {
					if (attr.AttributeType.Name == "ExtensionAttribute" && attr.AttributeType.Namespace == "System.Runtime.CompilerServices")
						return true;
				}
			}
			return false;
		}
		
		bool IsVisible(MethodAttributes att)
		{
			att &= MethodAttributes.MemberAccessMask;
			return IncludeInternalMembers
				|| att == MethodAttributes.Public
				|| att == MethodAttributes.Family
				|| att == MethodAttributes.FamORAssem;
		}
		
		static Accessibility GetAccessibility(MethodAttributes attr)
		{
			switch (attr & MethodAttributes.MemberAccessMask) {
				case MethodAttributes.Public:
					return Accessibility.Public;
				case MethodAttributes.FamANDAssem:
					return Accessibility.ProtectedAndInternal;
				case MethodAttributes.Assembly:
					return Accessibility.Internal;
				case MethodAttributes.Family:
					return Accessibility.Protected;
				case MethodAttributes.FamORAssem:
					return Accessibility.ProtectedOrInternal;
				default:
					return Accessibility.Private;
			}
		}
		
		void TranslateModifiers(MethodDefinition method, AbstractUnresolvedMember m)
		{
			if (m.DeclaringTypeDefinition.Kind == TypeKind.Interface) {
				// interface members don't have modifiers, but we want to handle them as "public abstract"
				m.Accessibility = Accessibility.Public;
				m.IsAbstract = true;
			} else {
				m.Accessibility = GetAccessibility(method.Attributes);
				if (method.IsAbstract) {
					m.IsAbstract = true;
					m.IsOverride = !method.IsNewSlot;
				} else if (method.IsFinal) {
					if (!method.IsNewSlot) {
						m.IsSealed = true;
						m.IsOverride = true;
					}
				} else if (method.IsVirtual) {
					if (method.IsNewSlot)
						m.IsVirtual = true;
					else
						m.IsOverride = true;
				}
				m.IsStatic = method.IsStatic;
			}
		}
		#endregion
		
		#region Read Parameter
		public IUnresolvedParameter ReadParameter(ParameterDefinition parameter)
		{
			if (parameter == null)
				throw new ArgumentNullException("parameter");
			var type = ReadTypeReference(parameter.ParameterType, typeAttributes: parameter, isFromSignature: true);
			var p = new DefaultUnresolvedParameter(type, interningProvider.Intern(parameter.Name));
			
			if (parameter.ParameterType is Mono.Cecil.ByReferenceType) {
				if (!parameter.IsIn && parameter.IsOut)
					p.IsOut = true;
				else
					p.IsRef = true;
			}
			AddAttributes(parameter, p);
			
			if (parameter.IsOptional) {
				p.DefaultValue = CreateSimpleConstantValue(type, parameter.Constant);
			}
			
			if (parameter.ParameterType is Mono.Cecil.ArrayType) {
				foreach (CustomAttribute att in parameter.CustomAttributes) {
					if (att.AttributeType.FullName == typeof(ParamArrayAttribute).FullName) {
						p.IsParams = true;
						break;
					}
				}
			}
			
			return interningProvider.Intern(p);
		}
		#endregion
		
		#region Read Field
		bool IsVisible(FieldAttributes att)
		{
			att &= FieldAttributes.FieldAccessMask;
			return IncludeInternalMembers
				|| att == FieldAttributes.Public
				|| att == FieldAttributes.Family
				|| att == FieldAttributes.FamORAssem;
		}

		decimal? TryDecodeDecimalConstantAttribute(CustomAttribute attribute)
		{
			if (attribute.ConstructorArguments.Count != 5)
				return null;

			BlobReader reader = new BlobReader(attribute.GetBlob(), null);
			if (reader.ReadUInt16() != 0x0001) {
				Debug.WriteLine("Unknown blob prolog");
				return null;
			}

			// DecimalConstantAttribute has the arguments (byte scale, byte sign, uint hi, uint mid, uint low) or (byte scale, byte sign, int hi, int mid, int low)
			// Both of these invoke the Decimal constructor (int lo, int mid, int hi, bool isNegative, byte scale) with explicit argument conversions if required.
			var ctorArgs = new object[attribute.ConstructorArguments.Count];
			for (int i = 0; i < ctorArgs.Length; i++) {
				switch (attribute.ConstructorArguments[i].Type.FullName) {
					case "System.Byte":
						ctorArgs[i] = reader.ReadByte();
						break;
					case "System.Int32":
						ctorArgs[i] = reader.ReadInt32();
						break;
					case "System.UInt32":
						ctorArgs[i] = unchecked((int)reader.ReadUInt32());
						break;
					default:
						return null;
				}
			}

			if (!ctorArgs.Select(a => a.GetType()).SequenceEqual(new[] { typeof(byte), typeof(byte), typeof(int), typeof(int), typeof(int) }))
				return null;

			return new decimal((int)ctorArgs[4], (int)ctorArgs[3], (int)ctorArgs[2], (byte)ctorArgs[1] != 0, (byte)ctorArgs[0]);
		}
		
		public IUnresolvedField ReadField(FieldDefinition field, IUnresolvedTypeDefinition parentType)
		{
			if (field == null)
				throw new ArgumentNullException("field");
			if (parentType == null)
				throw new ArgumentNullException("parentType");
			
			DefaultUnresolvedField f = new DefaultUnresolvedField(parentType, field.Name);
			f.Accessibility = GetAccessibility(field.Attributes);
			f.IsReadOnly = field.IsInitOnly;
			f.IsStatic = field.IsStatic;
			f.ReturnType = ReadTypeReference(field.FieldType, typeAttributes: field, isFromSignature: true);
			if (field.HasConstant) {
				f.ConstantValue = CreateSimpleConstantValue(f.ReturnType, field.Constant);
			}
			else {
				var decConstant = field.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.DecimalConstantAttribute");
				if (decConstant != null) {
					var constValue = TryDecodeDecimalConstantAttribute(decConstant);
					if (constValue != null)
						f.ConstantValue = CreateSimpleConstantValue(f.ReturnType, constValue);
				}
			}
			AddAttributes(field, f);
			
			RequiredModifierType modreq = field.FieldType as RequiredModifierType;
			if (modreq != null && modreq.ModifierType.FullName == typeof(IsVolatile).FullName) {
				f.IsVolatile = true;
			}
			
			FinishReadMember(f, field);
			return f;
		}
		
		static Accessibility GetAccessibility(FieldAttributes attr)
		{
			switch (attr & FieldAttributes.FieldAccessMask) {
				case FieldAttributes.Public:
					return Accessibility.Public;
				case FieldAttributes.FamANDAssem:
					return Accessibility.ProtectedAndInternal;
				case FieldAttributes.Assembly:
					return Accessibility.Internal;
				case FieldAttributes.Family:
					return Accessibility.Protected;
				case FieldAttributes.FamORAssem:
					return Accessibility.ProtectedOrInternal;
				default:
					return Accessibility.Private;
			}
		}
		#endregion
		
		#region Type Parameter Constraints
		void AddConstraints(DefaultUnresolvedTypeParameter tp, GenericParameter g)
		{
			switch (g.Attributes & GenericParameterAttributes.VarianceMask) {
				case GenericParameterAttributes.Contravariant:
					tp.Variance = VarianceModifier.Contravariant;
					break;
				case GenericParameterAttributes.Covariant:
					tp.Variance = VarianceModifier.Covariant;
					break;
			}
			
			tp.HasReferenceTypeConstraint = g.HasReferenceTypeConstraint;
			tp.HasValueTypeConstraint = g.HasNotNullableValueTypeConstraint;
			tp.HasDefaultConstructorConstraint = g.HasDefaultConstructorConstraint;
			
			if (g.HasConstraints) {
				foreach (TypeReference constraint in g.Constraints) {
					tp.Constraints.Add(ReadTypeReference(constraint));
				}
			}
		}
		#endregion
		
		#region Read Property

		Accessibility MergePropertyAccessibility (Accessibility left, Accessibility right)
		{
			if (left == Accessibility.Public || right == Accessibility.Public)
				return Accessibility.Public;

			if (left == Accessibility.ProtectedOrInternal || right == Accessibility.ProtectedOrInternal)
				return Accessibility.ProtectedOrInternal;

			if (left == Accessibility.Protected && right == Accessibility.Internal ||
			    left == Accessibility.Internal && right == Accessibility.Protected)
				return Accessibility.ProtectedOrInternal;

			if (left == Accessibility.Protected || right == Accessibility.Protected)
				return Accessibility.Protected;

			if (left == Accessibility.Internal || right == Accessibility.Internal)
				return Accessibility.Internal;

			if (left == Accessibility.ProtectedAndInternal || right == Accessibility.ProtectedAndInternal)
				return Accessibility.ProtectedAndInternal;

			return left;
		}

		public IUnresolvedProperty ReadProperty(PropertyDefinition property, IUnresolvedTypeDefinition parentType, SymbolKind propertyType = SymbolKind.Property)
		{
			if (property == null)
				throw new ArgumentNullException("property");
			if (parentType == null)
				throw new ArgumentNullException("parentType");
			DefaultUnresolvedProperty p = new DefaultUnresolvedProperty(parentType, property.Name);
			p.SymbolKind = propertyType;
			TranslateModifiers(property.GetMethod ?? property.SetMethod, p);
			if (property.GetMethod != null && property.SetMethod != null)
				p.Accessibility = MergePropertyAccessibility (GetAccessibility (property.GetMethod.Attributes), GetAccessibility (property.SetMethod.Attributes));

			p.ReturnType = ReadTypeReference(property.PropertyType, typeAttributes: property);
			
			p.Getter = ReadMethod(property.GetMethod, parentType, SymbolKind.Accessor, p);
			p.Setter = ReadMethod(property.SetMethod, parentType, SymbolKind.Accessor, p);
			
			if (property.HasParameters) {
				foreach (ParameterDefinition par in property.Parameters) {
					p.Parameters.Add(ReadParameter(par));
				}
			}
			AddAttributes(property, p);

			var accessor = p.Getter ?? p.Setter;
			if (accessor != null && accessor.IsExplicitInterfaceImplementation) {
				if (ShortenInterfaceImplNames)
					p.Name = property.Name.Substring(property.Name.LastIndexOf('.') + 1);
				p.IsExplicitInterfaceImplementation = true;
				foreach (var mr in accessor.ExplicitInterfaceImplementations) {
					p.ExplicitInterfaceImplementations.Add(new AccessorOwnerMemberReference(mr));
				}
			}

			FinishReadMember(p, property);
			return p;
		}
		#endregion
		
		#region Read Event
		public IUnresolvedEvent ReadEvent(EventDefinition ev, IUnresolvedTypeDefinition parentType)
		{
			if (ev == null)
				throw new ArgumentNullException("ev");
			if (parentType == null)
				throw new ArgumentNullException("parentType");
			
			DefaultUnresolvedEvent e = new DefaultUnresolvedEvent(parentType, ev.Name);
			TranslateModifiers(ev.AddMethod, e);
			e.ReturnType = ReadTypeReference(ev.EventType, typeAttributes: ev);
			
			e.AddAccessor    = ReadMethod(ev.AddMethod,    parentType, SymbolKind.Accessor, e);
			e.RemoveAccessor = ReadMethod(ev.RemoveMethod, parentType, SymbolKind.Accessor, e);
			e.InvokeAccessor = ReadMethod(ev.InvokeMethod, parentType, SymbolKind.Accessor, e);
			
			AddAttributes(ev, e);
			
			var accessor = e.AddAccessor ?? e.RemoveAccessor ?? e.InvokeAccessor;
			if (accessor != null && accessor.IsExplicitInterfaceImplementation) {
				if (ShortenInterfaceImplNames)
					e.Name = ev.Name.Substring(ev.Name.LastIndexOf('.') + 1);
				e.IsExplicitInterfaceImplementation = true;
				foreach (var mr in accessor.ExplicitInterfaceImplementations) {
					e.ExplicitInterfaceImplementations.Add(new AccessorOwnerMemberReference(mr));
				}
			}

			FinishReadMember(e, ev);
			
			return e;
		}
		#endregion
		
		#region FinishReadMember / Interning
		void FinishReadMember(AbstractUnresolvedMember member, MemberReference cecilDefinition)
		{
			member.MetadataToken = cecilDefinition.MetadataToken;
			member.ApplyInterningProvider(interningProvider);
			member.Freeze();
			RegisterCecilObject(member, cecilDefinition);
		}
		#endregion
		
		#region Type system translation table
		void RegisterCecilObject(IUnresolvedEntity typeSystemObject, MemberReference cecilObject)
		{
			if (OnEntityLoaded != null)
				OnEntityLoaded(typeSystemObject, cecilObject);
		}
		#endregion
	}
}
