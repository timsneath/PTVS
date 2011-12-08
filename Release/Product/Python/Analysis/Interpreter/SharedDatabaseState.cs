﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.PythonTools.Interpreter.Default;

namespace Microsoft.PythonTools.Interpreter {
    /// <summary>
    /// Cached state that's shared between multiple PythonTypeDatabase instances.
    /// </summary>
    class SharedDatabaseState : ITypeDatabaseReader {
        private readonly Dictionary<string, IPythonModule> _modules = new Dictionary<string, IPythonModule>();
        private readonly List<Action> _fixups = new List<Action>();
        private readonly string _dbDir;
        private readonly Dictionary<IPythonType, CPythonConstant> _constants = new Dictionary<IPythonType, CPythonConstant>();
        private readonly bool _is3x;
        private readonly Version _langVersion;  // language version, null when we have a generated database, set when using the shared DB.
        private IBuiltinPythonModule _builtinModule;

        public SharedDatabaseState(string databaseDirectory, bool is3x, IBuiltinPythonModule builtinsModule) {
            _dbDir = databaseDirectory;
            _modules["__builtin__"] = _builtinModule = builtinsModule ?? MakeBuiltinModule(databaseDirectory, is3x);
            _is3x = is3x;

            InitializeModules(databaseDirectory, is3x);
        }

        public SharedDatabaseState(string databaseDirectory, Version pythonLanguageVersion) {
            _dbDir = databaseDirectory;
            _is3x = pythonLanguageVersion.Major >= 3;
            _modules["__builtin__"] = _builtinModule = MakeBuiltinModule(databaseDirectory, _is3x);
            _langVersion = pythonLanguageVersion;

            InitializeModules(databaseDirectory, _is3x);
        }

        private void InitializeModules(string databaseDirectory, bool is3x) {
            foreach (var file in Directory.GetFiles(databaseDirectory)) {
                if (!file.EndsWith(".idb", StringComparison.OrdinalIgnoreCase) || file.IndexOf('$') != -1) {
                    continue;
                } else if (String.Equals(Path.GetFileName(file), is3x ? "builtins.idb" : "__builtin__.idb", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                string modName = Path.GetFileNameWithoutExtension(file);
                if (is3x && _langVersion != null) {
                    // aliases for 3.x when using the default completion DB
                    switch (modName) {
                        case "cPickle": modName = "_pickle"; break;
                        case "thread": modName = "_thread"; break;
                    }
                }
                _modules[modName] = new CPythonModule(this, modName, file, false);
            }
        }

        private CPythonBuiltinModule MakeBuiltinModule(string databaseDirectory, bool is3x) {
            string filename = Path.Combine(databaseDirectory, "__builtin__.idb");
            if (is3x && !File.Exists(filename)) {
                // Python 3.x the module is builtins, but we may have __builtin__.idb even if
                // we're 3.x when using the default completion DB that we install w/ PTVS.
                filename = Path.Combine(databaseDirectory, "builtins.idb");
            }

            return new CPythonBuiltinModule(this, "__builtin__", filename, true);
        }

        public IPythonModule GetModule(string name, PythonTypeDatabase instanceDb = null) {
            bool isInstance;
            return GetModule(name, out isInstance, instanceDb);
        }

        public IPythonModule GetModule(string name, out bool isInstanceMember, PythonTypeDatabase instanceDb = null) {
            IPythonModule res;
            if (_modules.TryGetValue(name, out res)) {
                isInstanceMember = false;
                return res;
            } else if (instanceDb != null && instanceDb._modules.TryGetValue(name, out res)) {
                isInstanceMember = true;
                return res;
            } else if (_is3x && _langVersion != null) {
                // aliases for 3.x when using the default completion DB
                switch (name) {
                    case "cPickle": return GetModule("_pickle", out isInstanceMember, instanceDb);
                    case "thread": return GetModule("_thread", out isInstanceMember, instanceDb);
                }
            }

            isInstanceMember = false;
            return null;
        }

        /// <summary>
        /// Looks up a type and queues a fixup if the type is not yet available.  Receives a delegate
        /// which assigns the value to the appropriate field.
        /// </summary>
        public void LookupType(object type, Action<IPythonType, bool> assign, PythonTypeDatabase instanceDb = null) {
            bool isInstance;
            var value = LookupType(type, out isInstance, instanceDb);

            if (value == null) {
                AddFixup(
                    () => {
                        var delayedType = LookupType(type, out isInstance, instanceDb);
                        if (delayedType == null) {
                            delayedType = BuiltinModule.GetAnyMember("object") as IPythonType;
                        }
                        Debug.Assert(delayedType != null);
                        assign(delayedType, isInstance);
                    }
                );
            } else {
                assign(value, isInstance);
            }
        }

        private IPythonType LookupType(object type, out bool isInstance, PythonTypeDatabase instanceDb) {
            if (type != null) {
                object[] typeInfo = (object[])type;
                if (typeInfo.Length == 2) {
                    string modName = typeInfo[0] as string;
                    string typeName = typeInfo[1] as string;

                    if (modName != null) {
                        if (typeName != null) {
                            var module = GetModule(modName, out isInstance, instanceDb);
                            if (module != null) {
                                IBuiltinPythonModule builtin = module as IBuiltinPythonModule;
                                if (builtin != null) {
                                    return builtin.GetAnyMember(typeName) as IPythonType;
                                }
                                return module.GetMember(null, typeName) as IPythonType;
                            }
                        }
                    }
                }
            } else {
                isInstance = false;
                return BuiltinModule.GetAnyMember("object") as IPythonType;
            }
            isInstance = false;
            return null;
        }

        public string GetBuiltinTypeName(BuiltinTypeId id) {
            string name;
            switch (id) {
                case BuiltinTypeId.Bool: name = "bool"; break;
                case BuiltinTypeId.Complex: name = "complex"; break;
                case BuiltinTypeId.Dict: name = "dict"; break;
                case BuiltinTypeId.Float: name = "float"; break;
                case BuiltinTypeId.Int: name = "int"; break;
                case BuiltinTypeId.List: name = "list"; break;
                case BuiltinTypeId.Long: name = "long"; break;
                case BuiltinTypeId.Object: name = "object"; break;
                case BuiltinTypeId.Set: name = "set"; break;
                case BuiltinTypeId.Str:
                    if (_is3x) {
                        name = "str";
                    } else {
                        name = "unicode";
                    }
                    break;
                case BuiltinTypeId.Bytes:
                    if (_is3x) {
                        name = "bytes";
                    } else {
                        name = "str";
                    }
                    break;
                case BuiltinTypeId.Tuple: name = "tuple"; break;
                case BuiltinTypeId.Type: name = "type"; break;

                case BuiltinTypeId.BuiltinFunction: name = "builtin_function"; break;
                case BuiltinTypeId.BuiltinMethodDescriptor: name = "builtin_method_descriptor"; break;
                case BuiltinTypeId.DictKeys: name = "dict_keys"; break;
                case BuiltinTypeId.DictValues: name = "dict_values"; break;
                case BuiltinTypeId.Function: name = "function"; break;
                case BuiltinTypeId.Generator: name = "generator"; break;
                case BuiltinTypeId.NoneType: name = "NoneType"; break;
                case BuiltinTypeId.Ellipsis: name = "ellipsis"; break;
                case BuiltinTypeId.Module: name = "module_type"; break;

                default: return null;
            }
            return name;
        }

        /// <summary>
        /// Adds a custom action which will attempt to resolve a type lookup which failed because the
        /// type was not yet defined.  All fixups are run after the database is loaded so all types
        /// should be available.
        /// </summary>
        private void AddFixup(Action action) {
            _fixups.Add(action);
        }

        /// <summary>
        /// Runs all of the custom fixup actions.
        /// </summary>
        public void RunFixups() {
            // we don't use foreach here because we can add fixups while
            // running fixups, in which case we want to keep processing
            // the additional fixups.
            for (int i = 0; i < _fixups.Count; i++) {
                _fixups[i]();
            }

            _fixups.Clear();
        }

        public void ReadMember(string memberName, Dictionary<string, object> memberValue, Action<string, IMember> assign, IMemberContainer container, PythonTypeDatabase instanceDb = null) {
            object memberKind;
            object value;
            Dictionary<string, object> valueDict;

            if (memberValue.TryGetValue("value", out value) &&
                (valueDict = (value as Dictionary<string, object>)) != null &&
                memberValue.TryGetValue("kind", out memberKind) && memberKind is string) {
                switch ((string)memberKind) {
                    case "function":
                        if (CheckVersion(valueDict)) {
                            assign(memberName, new CPythonFunction(this, memberName, valueDict, container));
                        }
                        break;
                    case "func_ref":
                        string funcName;
                        if (valueDict.TryGetValue("func_name", out value) && (funcName = value as string) != null) {
                            var names = funcName.Split('.');
                            IPythonModule mod;
                            if (_modules.TryGetValue(names[0], out mod) ||
                                (instanceDb != null && instanceDb._modules.TryGetValue(names[0], out mod))) {
                                if (names.Length == 2) {
                                    var mem = mod.GetMember(null, names[1]);
                                    if (mem == null) {
                                        AddFixup(() => {
                                            var tmp = mod.GetMember(null, names[1]);
                                            if (tmp != null) {
                                                assign(memberName, tmp);
                                            }
                                        });
                                    } else {
                                        assign(memberName, mem);
                                    }
                                } else {
                                    LookupType(new object[] { names[0], names[1] }, (type, fromInstanceDb) => {
                                        var mem = type.GetMember(null, names[2]);
                                        if (mem != null) {
                                            assign(memberName, mem);
                                        }
                                    }, instanceDb);
                                }
                            }
                        }
                        break;
                    case "method":
                        if (CheckVersion(valueDict)) {
                            assign(memberName, new CPythonMethodDescriptor(this, memberName, valueDict, container));
                        }
                        break;
                    case "property":
                        if (CheckVersion(valueDict)) {
                            assign(memberName, new CPythonProperty(this, valueDict, container));
                        }
                        break;
                    case "data":
                        object typeInfo;
                        if (valueDict.TryGetValue("type", out typeInfo) && CheckVersion(valueDict)) {
                            LookupType(
                                typeInfo,
                                (dataType, fromInstanceDb) => {
                                    assign(memberName, fromInstanceDb ? instanceDb.GetConstant(dataType) : GetConstant(dataType));
                                },
                                instanceDb
                            );
                        }
                        break;
                    case "type":
                        if (CheckVersion(valueDict)) {
                            assign(memberName, MakeType(memberName, valueDict, container));
                        }
                        break;
                    case "multiple":
                        object members;
                        object[] memsArray;
                        if (valueDict.TryGetValue("members", out members) && (memsArray = members as object[]) != null) {
                            IMember[] finalMembers = GetMultipleMembers(memberName, container, memsArray, instanceDb);
                            assign(memberName, new CPythonMultipleMembers(finalMembers));
                        }
                        break;
                    case "typeref":
                        object typeName;
                        if (valueDict.TryGetValue("type_name", out typeName)) {
                            LookupType(typeName, (dataType, fromInstanceDb) => {
                                assign(memberName, dataType);
                            }, instanceDb);
                        }
                        break;
                    case "moduleref":
                        object modName;
                        if (!valueDict.TryGetValue("module_name", out modName) || !(modName is string)) {
                            throw new InvalidOperationException("Failed to find module name: " + modName);
                        }

                        assign(memberName, GetModule((string)modName, instanceDb));
                        break;
                }
            }
        }

        private bool CheckVersion(Dictionary<string, object> valueDict) {
            object version;
            return !valueDict.TryGetValue("version", out version) || VersionApplies(version);
        }

        /// <summary>
        /// Checks to see if this member is applicable to our current language version for the shared DB.
        /// 
        /// Version formats are specified in the format:
        /// 
        /// version_check|version_checks
        /// 
        /// version_check:
        ///     greater_equals_check
        ///     less_equals_check
        ///     equals_check
        ///     
        /// greater_equals_check:   &gt;=version
        /// less_equals_check:      &lt;=version
        /// equals_check            ==version
        /// 
        /// version:    major_version.minor_version
        /// major_version: number
        /// minor_version: number
        /// 
        /// version_checks:  version_check(;version_check)+
        /// 
        /// For the member to be included all checks must pass.
        /// </summary>
        internal bool VersionApplies(object version) {
            if (_langVersion == null || version == null) {
                return true;
            }

            string strVer = version as string;
            if (strVer != null) {
                if (strVer.IndexOf(';') != -1) {
                    foreach (var curVersion in strVer.Split(';')) {
                        if (!OneVersionApplies(curVersion)) {
                            return false;
                        }
                    }
                    return true;
                } else {
                    return OneVersionApplies(strVer);
                }
            }
            return false;
        }

        private bool OneVersionApplies(string strVer) {            
            Version specifiedVer;
            if (strVer.StartsWith(">=")) {
                if (Version.TryParse(strVer.Substring(2), out specifiedVer) && _langVersion >= specifiedVer) {
                    return true;
                }
            } else if (strVer.StartsWith("<=")) {
                if (Version.TryParse(strVer.Substring(2), out specifiedVer) && _langVersion <= specifiedVer) {
                    return true;
                }
            } else if (strVer.StartsWith("==")) {
                if (Version.TryParse(strVer.Substring(2), out specifiedVer) && _langVersion == specifiedVer) {
                    return true;
                }
            }
            return false;
        }

        private IMember[] GetMultipleMembers(string memberName, IMemberContainer container, object[] memsArray, PythonTypeDatabase instanceDb = null) {
            IMember[] finalMembers = new IMember[memsArray.Length];
            for (int i = 0; i < finalMembers.Length; i++) {
                var curMember = memsArray[i] as Dictionary<string, object>;
                var tmp = i;    // close over the current value of i, not the last one...
                if (curMember != null) {
                    ReadMember(memberName, curMember, (name, newMemberValue) => finalMembers[tmp] = newMemberValue, container, instanceDb);
                }
            }
            return finalMembers;
        }

        private CPythonType MakeType(string typeName, Dictionary<string, object> valueDict, IMemberContainer container) {
            BuiltinTypeId typeId = BuiltinTypeId.Unknown;
            if (container == _builtinModule) {
                typeId = GetBuiltinTypeId(typeName);
            }

            return new CPythonType(container, this, typeName, valueDict, typeId);
        }

        private BuiltinTypeId GetBuiltinTypeId(string typeName) {
            switch (typeName) {
                case "list": return BuiltinTypeId.List;
                case "tuple": return BuiltinTypeId.Tuple;
                case "float": return BuiltinTypeId.Float;
                case "int": return BuiltinTypeId.Int;
                case "complex": return BuiltinTypeId.Complex;
                case "dict": return BuiltinTypeId.Dict;
                case "bool": return BuiltinTypeId.Bool;
                case "generator": return BuiltinTypeId.Generator;
                case "function": return BuiltinTypeId.Function;
                case "set": return BuiltinTypeId.Set;
                case "type": return BuiltinTypeId.Type;
                case "object": return BuiltinTypeId.Object;
                case "long": return BuiltinTypeId.Long;
                case "str":
                    if (_is3x) {
                        return BuiltinTypeId.Str;
                    }
                    return BuiltinTypeId.Bytes;
                case "unicode":
                    return BuiltinTypeId.Str;
                case "bytes":
                    return BuiltinTypeId.Bytes;
                case "builtin_function": return BuiltinTypeId.BuiltinFunction;
                case "builtin_method_descriptor": return BuiltinTypeId.BuiltinMethodDescriptor;
                case "NoneType": return BuiltinTypeId.NoneType;
                case "ellipsis": return BuiltinTypeId.Ellipsis;
                case "dict_keys": return BuiltinTypeId.DictKeys;
                case "dict_values": return BuiltinTypeId.DictValues;
            }
            return BuiltinTypeId.Unknown;
        }

        internal CPythonConstant GetConstant(IPythonType type) {
            CPythonConstant constant;
            if (!_constants.TryGetValue(type, out constant)) {
                _constants[type] = constant = new CPythonConstant(type);
            }
            return constant;
        }

        public IBuiltinPythonModule BuiltinModule {
            get {
                return _builtinModule;
            }
        }

        public Dictionary<string, IPythonModule> Modules {
            get {
                return _modules;
            }
        }

        public string DatabaseDirectory {
            get {
                return _dbDir;
            }
        }
    }
}