﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using de4dot.code.AssemblyClient;
using de4dot.blocks;

namespace de4dot.code {
	abstract class StringInlinerBase : MethodReturnValueInliner {
		protected override void inlineReturnValues(IList<CallResult> callResults) {
			foreach (var callResult in callResults) {
				var block = callResult.block;
				int num = callResult.callEndIndex - callResult.callStartIndex + 1;

				var decryptedString = callResult.returnValue as string;
				if (decryptedString == null)
					continue;

				int ldstrIndex = callResult.callStartIndex;
				block.replace(ldstrIndex, num, Instruction.Create(OpCodes.Ldstr, decryptedString));

				// If it's followed by castclass string, remove it
				if (ldstrIndex + 1 < block.Instructions.Count) {
					var instr = block.Instructions[ldstrIndex + 1];
					if (instr.OpCode.Code == Code.Castclass && instr.Operand.ToString() == "System.String")
						block.remove(ldstrIndex + 1, 1);
				}

				// If it's followed by String.Intern(), then nop out that call
				if (ldstrIndex + 1 < block.Instructions.Count) {
					var instr = block.Instructions[ldstrIndex + 1];
					if (instr.OpCode.Code == Code.Call) {
						var calledMethod = instr.Operand as MethodReference;
						if (calledMethod != null &&
							calledMethod.FullName == "System.String System.String::Intern(System.String)") {
							block.remove(ldstrIndex + 1, 1);
						}
					}
				}

				Log.v("Decrypted string: {0}", Utils.toCsharpString(decryptedString));
			}
		}
	}

	class DynamicStringInliner : StringInlinerBase {
		IAssemblyClient assemblyClient;
		Dictionary<int, int> methodTokenToId = new Dictionary<int, int>();

		class MyCallResult : CallResult {
			public int methodId;
			public GenericInstanceMethod gim;
			public MyCallResult(Block block, int callEndIndex, int methodId, GenericInstanceMethod gim)
				: base(block, callEndIndex) {
				this.methodId = methodId;
				this.gim = gim;
			}
		}

		public override bool HasHandlers {
			get { return methodTokenToId.Count != 0; }
		}

		public DynamicStringInliner(IAssemblyClient assemblyClient) {
			this.assemblyClient = assemblyClient;
		}

		public void init(IEnumerable<int> methodTokens) {
			methodTokenToId.Clear();
			foreach (var methodToken in methodTokens) {
				if (methodTokenToId.ContainsKey(methodToken))
					continue;
				methodTokenToId[methodToken] = assemblyClient.Service.defineStringDecrypter(methodToken);
			}
		}

		protected override CallResult createCallResult(MethodReference method, GenericInstanceMethod gim, Block block, int callInstrIndex) {
			int methodId;
			if (!methodTokenToId.TryGetValue(method.MetadataToken.ToInt32(), out methodId))
				return null;
			return new MyCallResult(block, callInstrIndex, methodId, gim);
		}

		protected override void inlineAllCalls() {
			var sortedCalls = new Dictionary<int, List<MyCallResult>>();
			foreach (var tmp in callResults) {
				var callResult = (MyCallResult)tmp;
				List<MyCallResult> list;
				if (!sortedCalls.TryGetValue(callResult.methodId, out list))
					sortedCalls[callResult.methodId] = list = new List<MyCallResult>(callResults.Count);
				list.Add(callResult);
			}

			foreach (var methodId in sortedCalls.Keys) {
				var list = sortedCalls[methodId];
				var args = new object[list.Count];
				for (int i = 0; i < list.Count; i++) {
					AssemblyData.SimpleData.pack(list[i].args);
					args[i] = list[i].args;
				}
				var decryptedStrings = assemblyClient.Service.decryptStrings(methodId, args, Method.MetadataToken.ToInt32());
				if (decryptedStrings.Length != args.Length)
					throw new ApplicationException("Invalid decrypted strings array length");
				AssemblyData.SimpleData.unpack(decryptedStrings);
				for (int i = 0; i < list.Count; i++)
					list[i].returnValue = (string)decryptedStrings[i];
			}
		}
	}

	class StaticStringInliner : StringInlinerBase {
		MethodDefinitionAndDeclaringTypeDict<Func<MethodDefinition, GenericInstanceMethod, object[], string>> stringDecrypters = new MethodDefinitionAndDeclaringTypeDict<Func<MethodDefinition, GenericInstanceMethod, object[], string>>();

		public override bool HasHandlers {
			get { return stringDecrypters.Count != 0; }
		}

		public IEnumerable<MethodDefinition> Methods {
			get { return stringDecrypters.getKeys(); }
		}

		class MyCallResult : CallResult {
			public MethodReference methodReference;
			public GenericInstanceMethod gim;
			public MyCallResult(Block block, int callEndIndex, MethodReference method, GenericInstanceMethod gim)
				: base(block, callEndIndex) {
				this.methodReference = method;
				this.gim = gim;
			}
		}

		public void add(MethodDefinition method, Func<MethodDefinition, GenericInstanceMethod, object[], string> handler) {
			if (method != null)
				stringDecrypters.add(method, handler);
		}

		protected override void inlineAllCalls() {
			foreach (var tmp in callResults) {
				var callResult = (MyCallResult)tmp;
				var handler = stringDecrypters.find(callResult.methodReference);
				callResult.returnValue = handler((MethodDefinition)callResult.methodReference, callResult.gim, callResult.args);
			}
		}

		protected override CallResult createCallResult(MethodReference method, GenericInstanceMethod gim, Block block, int callInstrIndex) {
			if (stringDecrypters.find(method) == null)
				return null;
			return new MyCallResult(block, callInstrIndex, method, gim);
		}
	}
}
