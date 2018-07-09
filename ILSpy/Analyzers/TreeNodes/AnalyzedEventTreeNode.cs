﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
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
using System.Linq;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy.TreeNodes;

namespace ICSharpCode.ILSpy.Analyzers.TreeNodes
{
	internal sealed class AnalyzedEventTreeNode : AnalyzerEntityTreeNode
	{
		readonly IEvent analyzedEvent;
		readonly string prefix;

		public AnalyzedEventTreeNode(IEvent analyzedEvent, string prefix = "")
		{
			this.analyzedEvent = analyzedEvent ?? throw new ArgumentNullException(nameof(analyzedEvent));
			this.prefix = prefix;
			this.LazyLoading = true;
		}

		public override IEntity Member => analyzedEvent;

		public override object Icon => EventTreeNode.GetIcon(analyzedEvent);

		// TODO: This way of formatting is not suitable for events which explicitly implement interfaces.
		public override object Text => prefix + Language.EventToString(analyzedEvent, includeTypeName: true, includeNamespace: true);

		protected override void LoadChildren()
		{
			if (analyzedEvent.AddAccessor != null)
				this.Children.Add(new AnalyzedAccessorTreeNode(analyzedEvent.AddAccessor, "add"));
			
			if (analyzedEvent.RemoveAccessor != null)
				this.Children.Add(new AnalyzedAccessorTreeNode(analyzedEvent.RemoveAccessor, "remove"));

			//foreach (var accessor in analyzedEvent.OtherMethods)
			//	this.Children.Add(new AnalyzedAccessorTreeNode(accessor, null));

			foreach (var lazy in App.ExportProvider.GetExports<IAnalyzer<IEvent>>()) {
				var analyzer = lazy.Value;
				if (analyzer.Show(analyzedEvent)) {
					this.Children.Add(new AnalyzerSearchTreeNode<IEvent>(analyzedEvent, analyzer));
				}
			}
		}
	}
}