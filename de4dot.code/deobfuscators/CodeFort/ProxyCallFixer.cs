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
using de4dot.blocks;

namespace de4dot.code.deobfuscators.CodeFort {
	class ProxyCallFixer : ProxyCallFixer3 {
		IList<MemberReference> memberReferences;
		MethodDefinitionAndDeclaringTypeDict<bool> proxyTargetMethods = new MethodDefinitionAndDeclaringTypeDict<bool>();
		TypeDefinition proxyMethodsType;

		public TypeDefinition ProxyMethodsType {
			get { return proxyMethodsType; }
		}

		public ProxyCallFixer(ModuleDefinition module)
			: base(module) {
		}

		public bool isProxyTargetMethod(MethodReference method) {
			return proxyTargetMethods.find(method);
		}

		public void findDelegateCreator() {
			foreach (var type in module.Types) {
				var creatorMethod = checkType(type);
				if (creatorMethod == null)
					continue;

				setDelegateCreatorMethod(creatorMethod);
				return;
			}
		}

		static MethodDefinition checkType(TypeDefinition type) {
			if (type.Fields.Count != 1)
				return null;
			if (type.Fields[0].FieldType.FullName != "System.Reflection.Module")
				return null;
			return checkMethods(type);
		}

		static MethodDefinition checkMethods(TypeDefinition type) {
			if (type.Methods.Count != 3)
				return null;

			MethodDefinition creatorMethod = null;
			foreach (var method in type.Methods) {
				if (method.Name == ".cctor")
					continue;
				if (DotNetUtils.isMethod(method, "System.Void", "(System.Int32)")) {
					creatorMethod = method;
					continue;
				}
				if (DotNetUtils.isMethod(method, "System.MulticastDelegate", "(System.Type,System.Reflection.MethodInfo,System.Int32)"))
					continue;

				return null;
			}
			return creatorMethod;
		}

		protected override object checkCctor(ref TypeDefinition type, MethodDefinition cctor) {
			var instrs = cctor.Body.Instructions;
			if (instrs.Count != 3)
				return null;
			var ldci4 = instrs[0];
			if (!DotNetUtils.isLdcI4(ldci4))
				return null;
			var call = instrs[1];
			if (call.OpCode.Code != Code.Call)
				return null;
			if (!isDelegateCreatorMethod(call.Operand as MethodDefinition))
				return null;
			int rid = DotNetUtils.getLdcI4Value(ldci4);
			if (cctor.DeclaringType.MetadataToken.RID != rid)
				throw new ApplicationException("Invalid rid");
			return rid;
		}

		protected override void getCallInfo(object context, FieldDefinition field, out MethodReference calledMethod, out OpCode callOpcode) {
			if (memberReferences == null)
				memberReferences = new List<MemberReference>(module.GetMemberReferences());

			int rid = 0;
			foreach (var c in field.Name)
				rid = (rid << 4) + hexToInt((char)((byte)c + 0x2F));
			rid &= 0x00FFFFFF;
			calledMethod = (MethodReference)memberReferences[rid - 1];
			var calledMethodDef = DotNetUtils.getMethod(module, calledMethod);
			if (calledMethodDef != null) {
				proxyMethodsType = calledMethodDef.DeclaringType;
				proxyTargetMethods.add(calledMethodDef, true);
				calledMethod = calledMethodDef;
			}
			callOpcode = OpCodes.Call;
		}

		static int hexToInt(char c) {
			if ('0' <= c && c <= '9')
				return c - '0';
			if ('a' <= c && c <= 'f')
				return c - 'a' + 10;
			if ('A' <= c && c <= 'F')
				return c - 'A' + 10;
			throw new ApplicationException("Invalid hex digit");
		}
	}
}
