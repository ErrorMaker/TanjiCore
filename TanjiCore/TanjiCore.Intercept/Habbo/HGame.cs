﻿using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;

using TanjiCore.Flazzy;
using TanjiCore.Flazzy.Tags;
using TanjiCore.Flazzy.ABC;
using TanjiCore.Flazzy.IO;
using TanjiCore.Flazzy.ABC.AVM2;
using TanjiCore.Flazzy.ABC.AVM2.Instructions;
using TanjiCore.Flazzy.Records;

namespace TanjiCore.Intercept.Habbo
{
    public enum Sanitization
    {
        None = 0,
        Deobfuscate = 1,
        RegisterRename = 2,
        IdentifierRename = 4,

        All = (Deobfuscate | RegisterRename | IdentifierRename)
    }

    public class HGame : ShockwaveFlash
    {
        private static readonly string[] _reservedNames = {
            "case", "class", "default", "do", "else",
            "extends", "false", "for", "get", "if",
            "implements", "import", "in", "package", "true",
            "try", "use", "var", "while", "with",
            "each", "null", "dynamic", "catch", "final",
            "break", "set", "static", "super", "include",
            "return", "native", "function", "throw", "switch" };

        private readonly Dictionary<DoABCTag, ABCFile> _abcFileTags;
        private readonly Dictionary<ASClass, MessageItem> _messages;

        public List<ABCFile> ABCFiles { get; }
        public bool IsPostShuffle { get; private set; } = true;

        public SortedDictionary<ushort, MessageItem> InMessages { get; }
        public SortedDictionary<ushort, MessageItem> OutMessages { get; }
        public SortedDictionary<string, List<MessageItem>> Messages { get; }

        private int _revisionIndex;
        public string Revision
        {
            get
            {
                if (ABCFiles.Count >= 3)
                {
                    return ABCFiles.Last().Pool.Strings[_revisionIndex];
                }
                else return "PRODUCTION-000000000000-000000000";
            }
            set
            {
                if (ABCFiles.Count >= 3)
                {
                    ABCFiles.Last().Pool.Strings[_revisionIndex] = value;
                }
            }
        }

        public HGame(string path)
            : this(File.OpenRead(path))
        { }
        public HGame(byte[] data)
            : this(new MemoryStream(data))

        { }
        public HGame(Stream input)
            : this(input, false)
        { }
        public HGame(Stream input, bool leaveOpen)
            : this(new FlashReader(input, leaveOpen))
        { }
        protected HGame(FlashReader input)
            : base(input)
        {
            _abcFileTags = new Dictionary<DoABCTag, ABCFile>();
            _messages = new Dictionary<ASClass, MessageItem>();

            ABCFiles = new List<ABCFile>();
            InMessages = new SortedDictionary<ushort, MessageItem>();
            OutMessages = new SortedDictionary<ushort, MessageItem>();
            Messages = new SortedDictionary<string, List<MessageItem>>();
        }

        public void Sanitize(Sanitization sanitization)
        {
            if (sanitization.HasFlag(Sanitization.Deobfuscate))
            {
                Deobfuscate();
            }
            if (sanitization.HasFlag(Sanitization.RegisterRename))
            {
                RenameRegisters();
            }
            if (sanitization.HasFlag(Sanitization.IdentifierRename))
            {
                RenameIdentifiers();
            }
        }
        #region Method Body Sanitization
        protected void Deobfuscate()
        {
            foreach (ABCFile abc in ABCFiles)
            {
                foreach (ASMethodBody body in abc.MethodBodies)
                {
                    if (body.Exceptions.Count > 0) continue;
                    if (body.Code[0] == 0x27 && body.Code[1] == 0x26) // PushFalse, PushTrue
                    {
                        ASCode code = body.ParseCode();
                        code.Deobfuscate();

                        body.Code = code.ToArray();
                    }
                }
            }
        }
        protected void RenameRegisters()
        {
            foreach (ABCFile abc in ABCFiles)
            {
                var nameIndices = new Dictionary<string, int>();
                foreach (ASMethodBody body in abc.MethodBodies)
                {
                    if (body.Exceptions.Count > 0) continue;
                    if (body.Code.Length <= 50 && !body.Code.Contains((byte)0xEF)) continue;

                    ASCode code = body.ParseCode();
                    if (!code.Contains(OPCode.Debug)) continue;

                    bool wasModified = false;
                    List<ASParameter> parameters = body.Method.Parameters;
                    foreach (DebugIns debug in code.GetOPGroup(OPCode.Debug))
                    {
                        if (debug.Name != "k") continue;

                        string name = string.Empty;
                        ASParameter parameter = null;
                        int register = debug.RegisterIndex;
                        if (register < parameters.Count)
                        {
                            parameter = parameters[register];
                            if (!string.IsNullOrWhiteSpace(parameter.Name))
                            {
                                nameIndices.Add(parameter.Name, parameter.NameIndex);
                                name = parameter.Name;
                            }
                            else name = $"param{register + 1}";
                        }
                        else name = $"local{(register - parameters.Count) + 1}";

                        if (!nameIndices.TryGetValue(name, out int nameIndex))
                        {
                            nameIndex = abc.Pool.AddConstant(name, false);
                            nameIndices.Add(name, nameIndex);
                        }
                        if (parameter != null)
                        {
                            parameter.NameIndex = nameIndex;
                        }

                        debug.NameIndex = nameIndex;
                        wasModified = true;
                    }
                    if (wasModified)
                    {
                        body.Code = code.ToArray();
                    }
                }
            }
        }
        protected void RenameIdentifiers()
        {
            // [InvalidNamespaceName] - [ValidNamespaceName]
            var validNamespaces = new Dictionary<string, string>();
            // [ValidNamespaceName.InvalidClassName] - [ValidClassName]
            var validClasses = new SortedDictionary<string, string>();

            int classCount = 0;
            int interfaceCount = 0;
            int namespaceCount = 0;
            foreach (ABCFile abc in ABCFiles)
            {
                var nameIndices = new Dictionary<string, int>();
                foreach (KeyValuePair<string, string> fixedNames in validNamespaces.Concat(validClasses))
                {
                    int index = abc.Pool.AddConstant(fixedNames.Value, false);
                    nameIndices.Add(fixedNames.Key, index);
                }

                #region Namespace Renaming
                foreach (ASNamespace @namespace in abc.Pool.Namespaces)
                {
                    if (@namespace == null) continue;

                    namespaceCount++;
                    if (IsValidIdentifier(@namespace.Name)) continue;

                    int validNameIndex = -1;
                    if (!nameIndices.TryGetValue(@namespace.Name, out validNameIndex))
                    {
                        string validName = ("Namespace_" + namespaceCount.ToString("0000"));
                        validNameIndex = abc.Pool.AddConstant(validName, false);

                        nameIndices.Add(@namespace.Name, validNameIndex);
                        if (!validNamespaces.ContainsKey(@namespace.Name))
                        {
                            validNamespaces.Add(@namespace.Name, validName);
                        }
                    }
                    else namespaceCount--;
                    @namespace.NameIndex = validNameIndex;
                }
                #endregion
                #region Class Renaming
                foreach (ASClass @class in abc.Classes)
                {
                    var validName = string.Empty;
                    ASInstance instance = @class.Instance;
                    if (instance.IsInterface)
                    {
                        validName = ("IInterface_" + (++interfaceCount).ToString("0000"));
                    }
                    else
                    {
                        validName = ("Class_" + (++classCount).ToString("0000"));
                    }

                    ASMultiname qname = instance.QName;
                    if (IsValidIdentifier(qname.Name)) continue;

                    int validNameIndex = -1;
                    string key = ($"{qname.Namespace.Name}.{qname.Name}");
                    if (!nameIndices.TryGetValue(key, out validNameIndex))
                    {
                        validNameIndex = abc.Pool.AddConstant(validName, false);

                        nameIndices.Add(key, validNameIndex);
                        if (!validClasses.ContainsKey(key))
                        {
                            validClasses.Add(key, validName);
                        }
                    }
                    else if (instance.IsInterface)
                    {
                        interfaceCount--;
                    }
                    else
                    {
                        classCount--;
                    }
                    qname.NameIndex = validNameIndex;
                }
                #endregion
                #region Multiname Renaming
                foreach (ASMultiname multiname in abc.Pool.Multinames)
                {
                    if (string.IsNullOrWhiteSpace(multiname?.Name)) continue;
                    if (IsValidIdentifier(multiname.Name)) continue;

                    var validClassKeys = new List<string>();
                    switch (multiname.Kind)
                    {
                        default: continue;
                        case MultinameKind.QName:
                        {
                            validClassKeys.Add($"{multiname.Namespace.Name}.{multiname.Name}");
                            break;
                        }
                        case MultinameKind.Multiname:
                        {
                            foreach (ASNamespace @namespace in multiname.NamespaceSet.GetNamespaces())
                            {
                                validClassKeys.Add($"{@namespace.Name}.{multiname.Name}");
                            }
                            break;
                        }
                    }
                    foreach (string key in validClassKeys)
                    {
                        int validNameIndex = -1;
                        if (nameIndices.TryGetValue(key, out validNameIndex))
                        {
                            multiname.NameIndex = validNameIndex;
                        }
                    }
                }
                #endregion

                abc.RebuildCache();
            }
            #region Symbol Renaming
            foreach (SymbolClassTag symbolTag in Tags
                .Where(t => t.Kind == TagKind.SymbolClass)
                .Cast<SymbolClassTag>())
            {
                for (int i = 0; i < symbolTag.Names.Count; i++)
                {
                    string fullName = symbolTag.Names[i];

                    string className = fullName;
                    var namespaceName = string.Empty;
                    string[] names = fullName.Split('.');
                    if (names.Length == 2)
                    {
                        className = names[1];
                        namespaceName = names[0];

                        if (IsValidIdentifier(namespaceName) &&
                            IsValidIdentifier(className))
                        {
                            continue;
                        }
                    }

                    var fixedFullName = string.Empty;
                    var fixedNamespaceName = string.Empty;
                    if (validNamespaces.TryGetValue(namespaceName, out fixedNamespaceName))
                    {
                        fixedFullName += (fixedNamespaceName + ".");
                    }
                    else if (!string.IsNullOrWhiteSpace(namespaceName))
                    {
                        fixedFullName += (namespaceName + ".");
                    }

                    var fixedClassName = string.Empty;
                    if (validClasses.TryGetValue($"{fixedNamespaceName}.{className}", out fixedClassName))
                    {
                        fixedFullName += fixedClassName;
                    }
                    else fixedFullName += className;
                    symbolTag.Names[i] = fixedFullName;
                }
            }
            #endregion
        }
        #endregion

        public void GenerateMessageHashes()
        {
            FindMessagesReferences();
            foreach (MessageItem message in OutMessages.Values.Concat(InMessages.Values))
            {
                message.GenerateHash();
                if (!Messages.TryGetValue(message.Hash, out List<MessageItem> group))
                {
                    group = new List<MessageItem>();
                    Messages.Add(message.Hash, group);
                }
                group.Add(message);
            }
        }
        #region Message Reference Searching
        private void FindMessagesReferences()
        {
            int classRank = 1;
            ABCFile abc = ABCFiles.Last();
            foreach (ASClass @class in abc.Classes)
            {
                ASInstance instance = @class.Instance;
                if (_messages.ContainsKey(@class)) continue;
                if (instance.Flags.HasFlag(ClassFlags.Interface)) continue;

                IEnumerable<ASMethod> methods = (new[] { @class.Constructor, instance.Constructor })
                    .Concat(instance.GetMethods())
                    .Concat(@class.GetMethods());

                int methodRank = 0;
                foreach (ASMethod fromMethod in methods)
                {
                    bool isStatic = (fromMethod.Trait?.IsStatic ?? @class.Constructor == fromMethod);
                    var fromContainer = (isStatic ? (ASContainer)@class : instance);

                    List<MessageReference> refernces = FindMessageReferences(fromContainer, fromMethod);
                    if (refernces.Count > 0)
                    {
                        methodRank++;
                    }
                    foreach (MessageReference reference in refernces)
                    {
                        reference.FromClass = @class;
                        reference.IsStatic = isStatic;
                        reference.ClassRank = classRank;
                        reference.MethodRank = methodRank;
                        reference.GroupCount = refernces.Count;
                    }
                }
                if (methodRank > 0)
                {
                    classRank++;
                }
            }

            var fromMethods = new List<ASMethod>();
            foreach (MessageItem incomingMsg in InMessages.Values)
            {
                foreach (MessageReference reference in incomingMsg.References)
                {
                    if (!fromMethods.Contains(reference.FromMethod))
                    {
                        fromMethods.Add(reference.FromMethod);
                    }
                }
            }
        }
        private List<MessageReference> FindMessageReferences(ASContainer fromContainer, ASMethod fromMethod)
        {
            int instructionRank = 0;
            ABCFile abc = fromMethod.GetABC();

            var nameStack = new Stack<ASMultiname>();
            var references = new List<MessageReference>();

            ASContainer container = null;
            ASCode code = fromMethod.Body.ParseCode();
            for (int i = 0; i < code.Count; i++)
            {
                int extraNamePopCount = 0;
                ASInstruction instruction = code[i];
                switch (instruction.OP)
                {
                    default: continue;
                    case OPCode.NewFunction:
                    {
                        var newFunction = (NewFunctionIns)instruction;
                        references.AddRange(FindMessageReferences(fromContainer, newFunction.Method));
                        continue;
                    }
                    case OPCode.GetProperty:
                    {
                        var getProperty = (GetPropertyIns)instruction;
                        nameStack.Push(getProperty.PropertyName);
                        continue;
                    }
                    case OPCode.GetLex:
                    {
                        var getLex = (GetLexIns)instruction;
                        container = abc.GetFirstClass(getLex.TypeName.Name);
                        continue;
                    }
                    case OPCode.GetLocal_0:
                    {
                        container = fromContainer;
                        continue;
                    }
                    case OPCode.ConstructProp:
                    {
                        var constructProp = (ConstructPropIns)instruction;

                        extraNamePopCount = constructProp.ArgCount;
                        nameStack.Push(constructProp.PropertyName);
                        break;
                    }
                }

                ASMultiname messageQName = nameStack.Pop();
                if (string.IsNullOrWhiteSpace(messageQName.Name)) continue;

                ASClass messageClass = abc.GetFirstClass(messageQName.Name);
                if (messageClass == null) continue;

                MessageItem message = null;
                if (!_messages.TryGetValue(messageClass, out message)) continue;
                if (message.HasMethodReference(fromMethod)) continue;

                var reference = new MessageReference();
                message.References.Add(reference);

                if (message.IsOutgoing)
                {
                    reference.FromMethod = fromMethod;
                    reference.InstructionRank = ++instructionRank;
                    reference.IsAnonymous = (!fromMethod.IsConstructor && fromMethod.Trait == null);

                    references.Add(reference);
                }
                else
                {
                    ASMultiname methodName = nameStack.Pop();
                    ASMethod callbackMethod = fromContainer.GetMethod(methodName.Name);
                    if (callbackMethod == null)
                    {
                        callbackMethod = container.GetMethod(methodName.Name);
                        if (callbackMethod == null)
                        {
                            ASMultiname slotName = nameStack.Pop();

                            ASTrait hostTrait = container.GetTraits(TraitKind.Slot)
                                .FirstOrDefault(st => st.QName == slotName);

                            container = abc.GetFirstInstance(hostTrait.Type.Name);
                            callbackMethod = container.GetMethod(methodName.Name);
                        }
                    }
                    reference.FromMethod = callbackMethod;
                }
            }
            return references;
        }
        #endregion

        public bool DisableHandshake()
        {
            if (!DisableEncryption()) return false;

            ABCFile abc = ABCFiles.Last();

            ASClass habboCommDemoClass = abc.GetFirstClass("HabboCommunicationDemo");
            if (habboCommDemoClass == null) return false;
            ASInstance habboCommDemoInstance = habboCommDemoClass.Instance;

            int firstCoerceIndex = 0;
            ASCode initCryptoCode = null;
            int asInterfaceQNameIndex = 0;
            ASMethod initCryptoMethod = null;
            foreach (ASMethod method in habboCommDemoInstance.GetMethods(1, "void"))
            {
                ASParameter parameter = method.Parameters[0];
                if (initCryptoCode == null &&
                    parameter.IsOptional &&
                    parameter.Type.Name == "Event")
                {
                    initCryptoMethod = method;
                    initCryptoCode = method.Body.ParseCode();
                    firstCoerceIndex = initCryptoCode.IndexOf(OPCode.Coerce);
                    asInterfaceQNameIndex = ((CoerceIns)initCryptoCode[firstCoerceIndex]).TypeNameIndex;
                }
                else if (parameter.TypeIndex == asInterfaceQNameIndex)
                {
                    int beforeExitIndex = (initCryptoCode.Count - 6);
                    initCryptoCode.RemoveRange(beforeExitIndex, 5);
                    initCryptoCode.InsertRange(beforeExitIndex, new ASInstruction[]
                    {
                        new GetLocal0Ins(),
                        new GetLocal2Ins(),
                        new CallPropVoidIns(abc)
                        {
                            ArgCount = 1,
                            PropertyNameIndex = method.Trait.QNameIndex
                        }
                    });
                    initCryptoMethod.Body.Code = initCryptoCode.ToArray();
                    return true;
                }
            }
            return false;
        }
        public bool DisableHostChecks()
        {
            ASMethod localHostCheckMethod = ABCFiles[0].Classes[0]
                .GetMethods(1, "Boolean").FirstOrDefault();

            if (localHostCheckMethod == null) return false;
            var hostCheckMethods = new List<ASMethod>
            {
                localHostCheckMethod
            };

            ASClass habboClass = ABCFiles[1].GetFirstClass("Habbo");
            if (habboClass == null) return false;
            ASInstance habboInstance = habboClass.Instance;

            ASMethod remoteHostCheckMethod = habboInstance.GetMethods(2, "Boolean")
                .Where(m => m.Parameters[0].Type.Name == "String" &&
                            m.Parameters[1].Type.Name == "Object")
                .FirstOrDefault();

            if (remoteHostCheckMethod == null) return false;
            hostCheckMethods.Add(remoteHostCheckMethod);

            foreach (ASMethod method in hostCheckMethods)
            {
                ASCode code = method.Body.ParseCode();
                if (!code.StartsWith(OPCode.PushTrue, OPCode.ReturnValue))
                {
                    code.InsertRange(0, new ASInstruction[]
                    {
                        new PushTrueIns(),
                        new ReturnValueIns()
                    });
                    method.Body.Code = code.ToArray();
                }
            }
            return DisableHostChanges();
        }
        public bool InjectKeyShouter(int id)
        {
            ABCFile abc = ABCFiles.Last();
            ASClass socketConnClass = abc.GetFirstClass("SocketConnection");

            if (socketConnClass == null) return false;
            ASInstance socketConnInstance = socketConnClass.Instance;

            ASTrait sendFunction = InjectUniversalSendFunction(true);
            if (sendFunction == null) return false;

            ASClass habboCommDemoClass = abc.GetFirstClass("HabboCommunicationDemo");
            if (habboCommDemoClass == null) return false;
            ASInstance habboCommDemoInstance = habboCommDemoClass.Instance;

            ASMethod pubKeyVerifyMethod = habboCommDemoInstance.GetMethods(1, "void")
                .Where(m => m.Body.MaxStack == 4 &&
                            m.Body.LocalCount == 10 &&
                            m.Body.MaxScopeDepth == 6 &&
                            m.Body.InitialScopeDepth == 5)
                .FirstOrDefault();
            if (pubKeyVerifyMethod == null) return false;

            int coereceCount = 0;
            ASCode pubKeyVerCode = pubKeyVerifyMethod.Body.ParseCode();
            foreach (ASInstruction instruct in pubKeyVerCode)
            {
                if (instruct.OP == OPCode.Coerce &&
                    (++coereceCount == 2))
                {
                    var coerceIns = (CoerceIns)instruct;
                    coerceIns.TypeNameIndex = socketConnInstance.QNameIndex;
                    break;
                }
            }
            pubKeyVerCode.InsertRange(pubKeyVerCode.Count - 5, new ASInstruction[]
            {
                new GetLocal2Ins(),
                new PushIntIns(abc, id),
                new GetLocalIns(6),
                new CallPropVoidIns(abc) { PropertyNameIndex = sendFunction.QNameIndex, ArgCount = 2 }
            });

            pubKeyVerifyMethod.Body.Code = pubKeyVerCode.ToArray();
            return true;
        }
        public bool InjectLoopbackEndpoint(int port)
        {
            ABCFile abc = ABCFiles.Last();

            ASInstance socketConnInstance = abc.GetFirstInstance("SocketConnection");
            if (socketConnInstance == null) return false;

            ASMethod initMethod = socketConnInstance.GetMethod(2, "init", "Boolean");
            if (initMethod == null) return false;

            ASCode code = initMethod.Body.ParseCode();
            for (int i = 0; i < code.Count; i++)
            {
                ASInstruction instruction = code[i];
                if (instruction.OP != OPCode.CallPropVoid) continue;

                var callPropVoid = (CallPropVoidIns)instruction;
                if (callPropVoid.PropertyName.Name != "connect") continue;

                code[i - 2] = new PushStringIns(abc, "127.0.0.1");
                code[i - 1] = new PushIntIns(abc, port);
                break;
            }
            initMethod.Body.Code = code.ToArray();
            return true;
        }
        public bool EnableDebugLogger(string functionName = null)
        {
            ABCFile abc = ABCFiles[1];

            ASMethod logMethod = null;
            foreach (ASClass @class in abc.Classes)
            {
                if (@class.Traits.Count != 2) continue;
                logMethod = @class.GetMethod(0, "log", "void");
                if (logMethod != null) break;
            }
            if (logMethod == null) return false;

            int externalInterfaceQNameIndex = abc.Pool.GetMultinameIndices("ExternalInterface").FirstOrDefault();
            if (externalInterfaceQNameIndex == 0) return false;

            int availableQNameIndex = abc.Pool.GetMultinameIndices("available").FirstOrDefault();
            if (availableQNameIndex == 0) return false;

            int callQNameIndex = abc.Pool.GetMultinameIndices("call").FirstOrDefault();
            if (callQNameIndex == 0) return false;

            ASCode code = logMethod.Body.ParseCode();
            int startIndex = (code.IndexOf(OPCode.PushScope) + 1);

            var ifNotAvailable = new IfFalseIns() { Offset = 1 };
            ASInstruction jumpExit = code[code.IndexOf(OPCode.ReturnVoid)];

            functionName = (functionName ?? "console.log");
            int functionNameIndex = abc.Pool.AddConstant(functionName, false);

            code.InsertRange(startIndex, new ASInstruction[]
            {
                new GetLexIns(abc) { TypeNameIndex = externalInterfaceQNameIndex },
                new GetPropertyIns(abc) { PropertyNameIndex = availableQNameIndex },
                ifNotAvailable,
                new GetLexIns(abc) { TypeNameIndex = externalInterfaceQNameIndex },
                new PushStringIns(abc, functionNameIndex),
                new GetLocal1Ins(),
                new CallPropVoidIns(abc) { ArgCount = 2, PropertyNameIndex = callQNameIndex }
            });

            code.JumpExits[ifNotAvailable] = jumpExit;
            logMethod.Body.Code = code.ToArray();

            logMethod.Body.MaxStack += 3;
            return true;
        }
        public bool InjectMessageLogger(string functionName = null)
        {
            ASClass coreClass = ABCFiles[1].GetFirstClass("Core");
            if (coreClass == null) return false;

            ASMethod debugMethod = coreClass.GetMethod(1, "debug", "void");
            if (debugMethod == null) return false;

            debugMethod.Flags |= MethodFlags.NeedRest;
            debugMethod.Parameters.Clear();

            ASCode debugCode = debugMethod.Body.ParseCode();
            for (int i = 0; i < debugCode.Count; i++)
            {
                ASInstruction instruction = debugCode[i];
                if (instruction.OP != OPCode.IfFalse) continue;

                ASInstruction[] block = debugCode.GetJumpBlock((Jumper)instruction);
                debugCode.RemoveRange((i - 1), (block.Length + 1));
                break;
            }

            // 'FlashExternalInterface.logDebug' is the default internal function name.
            if (!string.IsNullOrWhiteSpace(functionName))
            {
                int pushStringIndex = debugCode.IndexOf(OPCode.PushString);
                var pushStringIns = (PushStringIns)debugCode[pushStringIndex];
                pushStringIns.Value = functionName;
            }
            debugMethod.Body.Code = debugCode.ToArray();

            ABCFile abc = ABCFiles.Last();
            int coreQNameIndex = abc.Pool.GetMultinameIndices("Core").FirstOrDefault();
            ASMultiname coreQName = abc.Pool.Multinames[coreQNameIndex];

            int debugQNameIndex = abc.Pool.GetMultinameIndices("debug").FirstOrDefault();
            ASMultiname debugQName = abc.Pool.Multinames[debugQNameIndex];

            foreach (MessageItem message in OutMessages.Values)
            {
                ASInstance instance = message.Class.Instance;
                ASMethod arrayMethod = instance.GetMethods(0, "Array").FirstOrDefault();
                if (arrayMethod == null)
                {
                    if (instance.Super.Name != "Object")
                    {
                        instance = abc.GetFirstInstance(instance.Super.Name);
                        arrayMethod = instance.GetMethods(0, "Array").FirstOrDefault();
                    }
                    if (arrayMethod == null) return false;
                }

                if (arrayMethod.Body.Exceptions.Count > 0) continue;
                ASCode code = arrayMethod.Body.ParseCode();

                int returnValIndex = code.IndexOf(OPCode.ReturnValue);
                code.InsertRange(returnValIndex, new ASInstruction[]
                {
                    new DupIns(),
                    new SetLocal1Ins()
                });

                returnValIndex += 2;
                code.InsertRange(returnValIndex, new ASInstruction[]
                {
                    new GetLexIns(abc) { TypeNameIndex = coreQNameIndex },
                    new GetLocal1Ins(),
                    new CallPropVoidIns(abc) { ArgCount = 1, PropertyNameIndex = debugQNameIndex }
                });

                arrayMethod.Body.MaxStack += 3;
                arrayMethod.Body.LocalCount += 1;
                arrayMethod.Body.Code = code.ToArray();
            }
            return true;
        }
        public bool ReplaceRSAKeys(string exponent, string modulus)
        {
            ABCFile abc = ABCFiles.Last();
            ASClass keyObfuscatorClass = abc.GetFirstClass("KeyObfuscator");
            if (keyObfuscatorClass == null) return false;

            int modifyCount = 0;
            foreach (ASMethod method in keyObfuscatorClass.GetMethods(0, "String"))
            {
                int keyIndex = 0;
                switch (method.Trait.Id)
                {
                    // Get Modulus Method
                    case 6:
                    {
                        modifyCount++;
                        keyIndex = abc.Pool.AddConstant(modulus);
                        break;
                    }
                    // Get Exponent Method
                    case 7:
                    {
                        modifyCount++;
                        keyIndex = abc.Pool.AddConstant(exponent);
                        break;
                    }

                    // This is not a method we want to modify, continue enumerating.
                    default: continue;
                }

                ASCode code = method.Body.ParseCode();
                if (code.StartsWith(OPCode.PushString, OPCode.ReturnValue))
                {
                    code.RemoveRange(0, 2);
                }
                code.InsertRange(0, new ASInstruction[]
                {
                        new PushStringIns(abc, keyIndex),
                        new ReturnValueIns()
                });
                method.Body.Code = code.ToArray();
            }
            return (modifyCount == 2);
        }

        private void LoadMessages()
        {
            ABCFile abc = ABCFiles.Last();
            ASClass habboMessagesClass = abc.GetFirstClass("HabboMessages");
            if (habboMessagesClass == null)
            {
                IsPostShuffle = false;
                foreach (ASClass @class in abc.Classes)
                {
                    if (@class.Traits.Count != 2) continue;
                    if (@class.Instance.Traits.Count != 3) continue;
                    habboMessagesClass = @class;
                    break;
                }
            }

            ASCode code = habboMessagesClass.Constructor.Body.ParseCode();
            int inMapTypeIndex = habboMessagesClass.Traits[0].QNameIndex;
            int outMapTypeIndex = habboMessagesClass.Traits[1].QNameIndex;

            List<ASInstruction> instructions = code
                .Where(i => i.OP == OPCode.GetLex ||
                            i.OP == OPCode.PushShort ||
                            i.OP == OPCode.PushByte).ToList();

            for (int i = 0; i < instructions.Count; i += 3)
            {
                var getLexInst = (instructions[i + 0] as GetLexIns);
                bool isOutgoing = (getLexInst.TypeNameIndex == outMapTypeIndex);

                var primitive = (instructions[i + 1] as Primitive);
                ushort id = Convert.ToUInt16(primitive.Value);

                getLexInst = (instructions[i + 2] as GetLexIns);
                ASClass messageClass = abc.GetFirstClass(getLexInst.TypeName.Name);

                var message = new MessageItem(messageClass, isOutgoing, id);
                (isOutgoing ? OutMessages : InMessages).Add(id, message);

                if (_messages.ContainsKey(messageClass))
                {
                    _messages[messageClass].SharedIds.Add(id);
                }
                else _messages.Add(messageClass, message);

                if (id == 4000 && isOutgoing)
                {
                    ASInstance messageInstance = messageClass.Instance;
                    ASMethod toArrayMethod = messageInstance.GetMethods(0, "Array").First();

                    ASCode toArrayCode = toArrayMethod.Body.ParseCode();
                    int index = toArrayCode.IndexOf(OPCode.PushString);

                    if (index != -1)
                    {
                        var pushStringIns = (PushStringIns)toArrayCode[index];
                        _revisionIndex = pushStringIns.ValueIndex;
                    }
                }
            }
        }
        private void SimplifySendCode(ABCFile abc, ASCode sendCode)
        {
            bool isTrimming = true;
            for (int i = 0; i < sendCode.Count; i++)
            {
                ASInstruction instruction = sendCode[i];
                if (!isTrimming && Local.IsValid(instruction.OP))
                {
                    var local = (Local)instruction;
                    int newRegister = (local.Register - 1);
                    if (newRegister < 1) continue;

                    ASInstruction replacement = null;
                    if (Local.IsGetLocal(local.OP))
                    {
                        replacement = new GetLocalIns(newRegister);
                    }
                    else if (Local.IsSetLocal(local.OP))
                    {
                        replacement = new SetLocalIns(newRegister);
                    }
                    sendCode[i] = replacement;
                }
                else if (isTrimming)
                {
                    if (instruction.OP != OPCode.CallProperty) continue;
                    var callProperty = (CallPropertyIns)instruction;
                    if (callProperty.PropertyName.Name != "encode") continue;

                    sendCode.RemoveRange(0, i - 4);
                    int idNameIndex = abc.Pool.AddConstant("id");
                    int valuesNameIndex = abc.Pool.AddConstant("values");
                    sendCode.InsertRange(0, new ASInstruction[]
                    {
                        new GetLocal0Ins(),
                        new PushScopeIns(),
                        new DebugIns(abc, idNameIndex, 1, 0),
                        new DebugIns(abc, valuesNameIndex, 1, 1)
                    });

                    i = 0;
                    isTrimming = false;
                }
            }
        }
        private ASTrait InjectUniversalSendFunction(bool disableCrypto)
        {
            ABCFile abc = ABCFiles.Last();
            if (disableCrypto && !DisableEncryption()) return null;

            ASClass socketConnClass = abc.GetFirstClass("SocketConnection");
            if (socketConnClass == null) return null;
            ASInstance socketConnInstance = socketConnClass.Instance;

            ASMethod sendMethod = socketConnInstance.GetMethod(1, "send", "Boolean");
            if (sendMethod == null) return null;

            ASTrait sendFunctionTrait = socketConnInstance.GetMethod(1, "sendMessage", "Boolean")?.Trait;
            if (sendFunctionTrait != null) return sendFunctionTrait;

            ASCode sendCode = sendMethod.Body.ParseCode();
            SimplifySendCode(abc, sendCode);

            var sendMessageMethod = new ASMethod(abc);
            sendMessageMethod.Flags |= MethodFlags.NeedRest;
            sendMessageMethod.ReturnTypeIndex = sendMethod.ReturnTypeIndex;
            int sendMessageMethodIndex = abc.AddMethod(sendMessageMethod);

            // The parameters for the instructions to expect / use.
            var idParam = new ASParameter(abc, sendMessageMethod);
            idParam.NameIndex = abc.Pool.AddConstant("id");
            idParam.TypeIndex = abc.Pool.GetMultinameIndices("int").First();
            sendMessageMethod.Parameters.Add(idParam);

            // The method body that houses the instructions.
            var sendMessageBody = new ASMethodBody(abc);
            sendMessageBody.MethodIndex = sendMessageMethodIndex;
            sendMessageBody.Code = sendCode.ToArray();
            sendMessageBody.InitialScopeDepth = 5;
            sendMessageBody.MaxScopeDepth = 6;
            sendMessageBody.LocalCount = 10;
            sendMessageBody.MaxStack = 5;
            abc.AddMethodBody(sendMessageBody);

            socketConnInstance.AddMethod(sendMessageMethod, "sendMessage");
            return sendMessageMethod.Trait;
        }

        private bool DisableEncryption()
        {
            ASClass socketConnClass = ABCFiles.Last().GetFirstClass("SocketConnection");
            if (socketConnClass == null) return false;
            ASInstance socketConnInstance = socketConnClass.Instance;

            ASMethod sendMethod = socketConnInstance.GetMethod(1, "send", "Boolean");
            if (sendMethod == null) return false;

            ASCode sendCode = sendMethod.Body.ParseCode();
            sendCode.Deobfuscate();

            ASTrait socketSlot = socketConnInstance.GetSlotTraits("Socket").FirstOrDefault();
            if (socketSlot == null) return false;

            int encodedLocal = -1;
            for (int i = 0; i < sendCode.Count; i++)
            {
                ASInstruction instruction = sendCode[i];
                if (instruction.OP != OPCode.CallProperty) continue;

                var callProperty = (CallPropertyIns)instruction;
                if (callProperty.PropertyName.Name != "encode") continue;

                instruction = sendCode[i += 2];
                if (!Local.IsSetLocal(instruction.OP)) continue;

                encodedLocal = ((Local)instruction).Register;
                sendCode.RemoveRange(i + 1, sendCode.Count - (i + 1));
                break;
            }
            if (encodedLocal == -1) return false;
            ABCFile abc = socketConnInstance.GetABC();

            int flushIndex = abc.Pool.GetMultinameIndices("flush").First();
            if (flushIndex == 0) return false;

            int writeBytesIndex = abc.Pool.GetMultinameIndices("writeBytes").First();
            if (writeBytesIndex == 0) return false;

            sendCode.AddRange(new ASInstruction[]
            {
                new GetLocal0Ins(),
                new GetPropertyIns(abc) { PropertyNameIndex = socketSlot.QNameIndex },
                new GetLocalIns(encodedLocal),
                new CallPropVoidIns(abc) { PropertyNameIndex = writeBytesIndex, ArgCount = 1 },


                new GetLocal0Ins(),
                new GetPropertyIns(abc) { PropertyNameIndex = socketSlot.QNameIndex },
                new CallPropVoidIns(abc) { PropertyNameIndex = flushIndex },

                new PushTrueIns(),
                new ReturnValueIns()
            });
            sendMethod.Body.Code = sendCode.ToArray();
            return true;
        }
        private bool DisableHostChanges()
        {
            ABCFile abc = ABCFiles.Last();

            ASClass habboCommMngrClass = abc.GetFirstClass("HabboCommunicationManager");
            if (habboCommMngrClass == null) return false;
            ASInstance habboCommMngrInstance = habboCommMngrClass.Instance;

            ASTrait infoHostSlot = habboCommMngrInstance
                .GetSlotTraits("String").FirstOrDefault();
            if (infoHostSlot == null) return false;

            int getPropertyNameIndex = abc.Pool
                .GetMultinameIndices("getProperty").FirstOrDefault();
            if (getPropertyNameIndex == 0) return false;

            ASMethod initComponentMethod =
                habboCommMngrInstance.GetMethod(0, "initComponent", "void");
            if (initComponentMethod == null) return false;

            string connectMethodName = string.Empty;
            ASCode initComponentCode = initComponentMethod.Body.ParseCode();
            for (int i = initComponentCode.Count - 1; i >= 0; i--)
            {
                ASInstruction instruction = initComponentCode[i];
                if (instruction.OP != OPCode.CallPropVoid) continue;

                var callPropVoidIns = (CallPropVoidIns)instruction;
                connectMethodName = callPropVoidIns.PropertyName.Name;
                break;
            }
            if (string.IsNullOrWhiteSpace(connectMethodName)) return false;

            ASMethod connectMethod = habboCommMngrInstance
                .GetMethod(0, connectMethodName, "void");
            if (connectMethod == null) return false;

            ASCode connectCode = connectMethod.Body.ParseCode();
            connectCode.InsertRange(4, new ASInstruction[]
            {
                new GetLocal0Ins(),
                new FindPropStrictIns(abc, getPropertyNameIndex),
                new PushStringIns(abc, "connection.info.host"),
                new CallPropertyIns(abc, getPropertyNameIndex, 1),
                new InitPropertyIns(abc, infoHostSlot.QNameIndex)
                // Inserts: this.Slot = getProperty("connection.info.host");
                // This portion ensures the host slot value stays vanilla(no prefixes/changes).
            });

            // This portion prevents any suffix from being added to the host slot.
            int magicInverseIndex = abc.Pool.AddConstant(65290);
            foreach (ASInstruction instruction in connectCode)
            {
                if (instruction.OP != OPCode.PushInt) continue;

                var pushIntIns = (PushIntIns)instruction;
                pushIntIns.ValueIndex = magicInverseIndex;
            }
            connectMethod.Body.Code = connectCode.ToArray();
            return true;
        }

        public override void Disassemble(Action<TagItem> callback)
        {
            base.Disassemble(callback);
            LoadMessages();
        }
        protected override void WriteTag(TagItem tag, FlashWriter output)
        {
            if (tag.Kind == TagKind.DoABC)
            {
                var doABCTag = (DoABCTag)tag;
                doABCTag.ABCData = _abcFileTags[doABCTag].ToArray();
            }
            base.WriteTag(tag, output);
        }
        protected override TagItem ReadTag(HeaderRecord header, FlashReader input)
        {
            TagItem tag = base.ReadTag(header, input);
            if (tag.Kind == TagKind.DoABC)
            {
                var doABCTag = (DoABCTag)tag;
                var abcFile = new ABCFile(doABCTag.ABCData);

                _abcFileTags[doABCTag] = abcFile;
                ABCFiles.Add(abcFile);
            }
            return tag;
        }

        public static bool IsValidIdentifier(string value, bool invalidOnSanitized = false)
        {
            value = value.ToLower();
            if (invalidOnSanitized &&
                (value.StartsWith("class_") ||
                value.StartsWith("iinterface_") ||
                value.StartsWith("namespace_") ||
                value.StartsWith("method_") ||
                value.StartsWith("constant_") ||
                value.StartsWith("slot_") ||
                value.StartsWith("param")))
            {
                return false;
            }

            return (!value.Contains("_-") &&
                !_reservedNames.Contains(value.Trim()));
        }
    }
    public class MessageItem
    {
        public ushort Id { get; set; }
        public bool IsOutgoing { get; set; }
        public string Hash { get; private set; }

        public ASClass Class { get; }
        public ASClass Parser { get; }
        public string[] Structure { get; }
        public List<ushort> SharedIds { get; }
        public List<MessageReference> References { get; }

        private MessageItem(bool isOutgoing, ushort id)
        {
            Id = id;
            IsOutgoing = isOutgoing;

            SharedIds = new List<ushort>();
            References = new List<MessageReference>();
        }
        public MessageItem(ASClass messageClass, bool isOutgoing, ushort id)
            : this(isOutgoing, id)
        {
            Class = messageClass;
            if (!IsOutgoing)
            {
                Parser = GetMessageParser();
                if (Parser != null)
                {
                    Structure = GetIncomingStructure(Parser);
                }
            }
            else
            {
                Structure = GetOutgoingStructure(Class);
            }
        }

        public string GenerateHash()
        {
            using (var output = new MessageHasher(false))
            {
                output.Write(IsOutgoing);
                string name = Class.QName.Name;
                if (!HGame.IsValidIdentifier(name, true))
                {
                    output.Write(References.Count);
                    foreach (MessageReference reference in References)
                    {
                        output.Write(reference.IsStatic);
                        output.Write(reference.GroupCount);
                        if (reference.FromMethod != null)
                        {
                            output.Write(reference.FromMethod);
                        }
                        output.Write(reference.IsAnonymous);

                        output.Write(reference.ClassRank);
                        output.Write(reference.MethodRank);

                        if (IsOutgoing)
                        {
                            output.Write(reference.InstructionRank);
                        }

                        if (reference.FromClass != null)
                        {
                            output.Write(reference.FromClass, false);
                        }
                    }
                }
                else output.Write(name);
                return (Hash = output.GetHash());
            }
        }
        public bool HasMethodReference(ASMethod method)
        {
            return References.Any(r => r.FromMethod == method);
        }

        private ASClass GetMessageParser()
        {
            ABCFile abc = Class.GetABC();
            ASInstance instance = Class.Instance;

            ASInstance superInstance = abc.GetFirstInstance(instance.Super.Name);
            if (superInstance == null) superInstance = instance;

            ASMethod parserGetterMethod = superInstance.GetGetter("parser")?.Method;
            if (parserGetterMethod == null) return null;

            IEnumerable<ASMethod> methods = instance.GetMethods();
            foreach (ASMethod method in methods.Concat(new[] { instance.Constructor }))
            {
                ASCode code = method.Body.ParseCode();
                foreach (ASInstruction instruction in code)
                {
                    ASMultiname multiname = null;
                    if (instruction.OP == OPCode.FindPropStrict)
                    {
                        var findPropStrictIns = (FindPropStrictIns)instruction;
                        multiname = findPropStrictIns.PropertyName;
                    }
                    else if (instruction.OP == OPCode.GetLex)
                    {
                        var getLexIns = (GetLexIns)instruction;
                        multiname = getLexIns.TypeName;
                    }
                    else continue;

                    foreach (ASClass refClass in abc.GetClasses(multiname.Name))
                    {
                        ASInstance refInstance = refClass.Instance;
                        if (refInstance.ContainsInterface(parserGetterMethod.ReturnType.Name))
                        {
                            return refClass;
                        }
                    }
                }
            }
            return null;
        }

        #region Structure Extraction
        private string[] GetIncomingStructure(ASClass @class)
        {
            ASMethod parseMethod = @class.Instance.GetMethod(1, "parse", "Boolean");
            return GetIncomingStructure(@class.Instance, parseMethod);
        }
        private string[] GetIncomingStructure(ASInstance instance, ASMethod method)
        {
            if (method.Body.Exceptions.Count > 0) return null;

            ASCode code = method.Body.ParseCode();
            if (code.JumpExits.Count > 0 || code.SwitchExits.Count > 0) return null;

            ABCFile abc = method.GetABC();
            var structure = new List<string>();
            for (int i = 0; i < code.Count; i++)
            {
                ASInstruction instruction = code[i];
                if (instruction.OP != OPCode.GetLocal_1) continue;

                ASInstruction next = code[++i];
                switch (next.OP)
                {
                    case OPCode.CallProperty:
                    {
                        var callProperty = (CallPropertyIns)next;
                        if (callProperty.ArgCount > 0)
                        {
                            ASMultiname propertyName = null;
                            ASInstruction previous = code[i - 2];

                            switch (previous.OP)
                            {
                                case OPCode.GetLex:
                                {
                                    var getLex = (GetLexIns)previous;
                                    propertyName = getLex.TypeName;
                                    break;
                                }

                                case OPCode.ConstructProp:
                                {
                                    var constructProp = (ConstructPropIns)previous;
                                    propertyName = constructProp.PropertyName;
                                    break;
                                }

                                case OPCode.GetLocal_0:
                                {
                                    propertyName = instance.QName;
                                    break;
                                }
                            }

                            ASInstance innerInstance = abc.GetFirstInstance(propertyName.Name);
                            ASMethod innerMethod = innerInstance.GetMethod(callProperty.ArgCount, callProperty.PropertyName.Name);
                            if (innerMethod == null)
                            {
                                ASClass innerClass = abc.GetFirstClass(propertyName.Name);
                                innerMethod = innerClass.GetMethod(callProperty.ArgCount, callProperty.PropertyName.Name);
                            }

                            string[] innerStructure = GetIncomingStructure(innerInstance, innerMethod);
                            if (innerStructure != null)
                            {
                                structure.AddRange(innerStructure);
                            }
                            else return null;
                        }
                        else
                        {
                            structure.Add(GetReadReturnTypeName(callProperty.PropertyName));
                        }
                        break;
                    }

                    case OPCode.ConstructProp:
                    {
                        var constructProp = (ConstructPropIns)next;
                        ASInstance innerInstance = abc.GetFirstInstance(constructProp.PropertyName.Name);

                        string[] innerStructure = GetIncomingStructure(innerInstance, innerInstance.Constructor);
                        if (innerStructure != null)
                        {
                            structure.AddRange(innerStructure);
                        }
                        else return null;
                        break;
                    }

                    case OPCode.ConstructSuper:
                    {
                        var constructSuper = (ConstructSuperIns)next;
                        ASInstance superInstance = abc.GetFirstInstance(instance.Super.Name);

                        string[] innerStructure = GetIncomingStructure(superInstance, superInstance.Constructor);
                        if (innerStructure != null)
                        {
                            structure.AddRange(innerStructure);
                        }
                        else return null;
                        break;
                    }

                    case OPCode.CallSuper:
                    {
                        var callSuper = (CallSuperIns)next;
                        ASInstance superInstance = abc.GetFirstInstance(instance.Super.Name);

                        ASMethod superMethod = superInstance.GetMethod(callSuper.ArgCount, callSuper.MethodName.Name);
                        string[] innerStructure = GetIncomingStructure(superInstance, superMethod);
                        if (innerStructure != null)
                        {
                            structure.AddRange(innerStructure);
                        }
                        else return null;
                        break;
                    }

                    case OPCode.CallPropVoid:
                    {
                        var callPropVoid = (CallPropVoidIns)next;
                        if (callPropVoid.ArgCount == 0)
                        {
                            structure.Add(GetReadReturnTypeName(callPropVoid.PropertyName));
                        }
                        else return null;
                        break;
                    }

                    default: return null;
                }
            }
            return structure.ToArray();
        }

        private string[] GetOutgoingStructure(ASClass @class)
        {
            ASMethod getArrayMethod = @class.Instance.GetMethods(0, "Array").FirstOrDefault();
            if (getArrayMethod == null)
            {
                ASClass superClass = @class.GetABC().GetFirstClass(@class.Instance.Super.Name);
                return GetOutgoingStructure(superClass);
            }
            if (getArrayMethod.Body.Exceptions.Count > 0) return null;
            ASCode getArrayCode = getArrayMethod.Body.ParseCode();

            if (getArrayCode.JumpExits.Count > 0 ||
                getArrayCode.SwitchExits.Count > 0)
            {
                // Unable to parse data structure that relies on user input that is not present,
                // since the structure may change based on the provided input.
                return null;
            }

            ASInstruction resultPusher = null;
            for (int i = getArrayCode.Count - 1; i >= 0; i--)
            {
                ASInstruction instruction = getArrayCode[i];
                if (instruction.OP == OPCode.ReturnValue)
                {
                    resultPusher = getArrayCode[i - 1];
                    break;
                }
            }

            int argCount = -1;
            if (resultPusher.OP == OPCode.ConstructProp)
            {
                argCount = ((ConstructPropIns)resultPusher).ArgCount;
            }
            else if (resultPusher.OP == OPCode.NewArray)
            {
                argCount = ((NewArrayIns)resultPusher).ArgCount;
            }

            if (argCount > 0)
            {
                return GetOutgoingStructure(getArrayCode, resultPusher, argCount);
            }
            else if (argCount == 0 ||
                resultPusher.OP == OPCode.PushNull)
            {
                return null;
            }

            if (resultPusher.OP == OPCode.GetProperty)
            {
                var getProperty = (GetPropertyIns)resultPusher;
                return GetOutgoingStructure(Class, getProperty.PropertyName);
            }
            else if (Local.IsGetLocal(resultPusher.OP))
            {
                return GetOutgoingStructure(getArrayCode, (Local)resultPusher);
            }
            return null;
        }
        private string[] GetOutgoingStructure(ASCode code, Local getLocal)
        {
            var structure = new List<string>();
            for (int i = 0; i < code.Count; i++)
            {
                ASInstruction instruction = code[i];
                if (instruction == getLocal) break;
                if (!Local.IsGetLocal(instruction.OP)) continue;

                var local = (Local)instruction;
                if (local.Register != getLocal.Register) continue;

                for (i += 1; i < code.Count; i++)
                {
                    ASInstruction next = code[i];
                    if (next.OP != OPCode.CallPropVoid) continue;

                    var callPropVoid = (CallPropVoidIns)next;
                    if (callPropVoid.PropertyName.Name != "push") continue;

                    ASInstruction previous = code[i - 1];
                    if (previous.OP == OPCode.GetProperty)
                    {
                        ASClass classToCheck = Class;
                        var getProperty = (GetPropertyIns)previous;
                        ASMultiname propertyName = getProperty.PropertyName;

                        ASInstruction beforeGetProp = code[i - 2];
                        if (beforeGetProp.OP == OPCode.GetLex)
                        {
                            var getLex = (GetLexIns)beforeGetProp;
                            classToCheck = classToCheck.GetABC().GetFirstClass(getLex.TypeName.Name);
                        }

                        if (TryGetTraitTypeName(classToCheck, propertyName, out string propertyTypeName) ||
                            TryGetTraitTypeName(classToCheck.Instance, propertyName, out propertyTypeName))
                        {
                            structure.Add(propertyTypeName);
                        }
                    }
                }
            }
            return structure.ToArray();
        }
        private string[] GetOutgoingStructure(ASClass @class, ASMultiname propertyName)
        {
            ASMethod constructor = @class.Instance.Constructor;
            if (constructor.Body.Exceptions.Count > 0) return null;
            ASCode code = constructor.Body.ParseCode();

            if (code.JumpExits.Count > 0 ||
                code.SwitchExits.Count > 0)
            {
                return null;
            }

            var structure = new List<string>();
            var pushedLocals = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++)
            {
                ASInstruction next = null;
                ASInstruction instruction = code[i];
                if (instruction.OP == OPCode.NewArray)
                {
                    var newArray = (NewArrayIns)instruction;
                    if (newArray.ArgCount > 0)
                    {
                        var structArray = new string[newArray.ArgCount];
                        for (int j = i - 1, length = newArray.ArgCount; j >= 0; j--)
                        {
                            ASInstruction previous = code[j];
                            if (Local.IsGetLocal(previous.OP) &&
                                previous.OP != OPCode.GetLocal_0)
                            {
                                var local = (Local)previous;
                                ASParameter parameter = constructor.Parameters[local.Register - 1];
                                structArray[--length] = parameter.Type.Name;
                            }
                            if (length == 0)
                            {
                                structure.AddRange(structArray);
                                break;
                            }
                        }
                    }
                }
                else if (instruction.OP == OPCode.ConstructSuper)
                {
                    var constructSuper = (ConstructSuperIns)instruction;
                    if (constructSuper.ArgCount > 0)
                    {
                        ASClass superClass = @class.GetABC().GetFirstClass(@class.Instance.Super.Name);
                        structure.AddRange(GetOutgoingStructure(superClass, propertyName));
                    }
                }
                if (instruction.OP != OPCode.GetProperty) continue;

                var getProperty = (GetPropertyIns)instruction;
                if (getProperty.PropertyName != propertyName) continue;

                next = code[++i];
                ASClass classToCheck = @class;
                if (Local.IsGetLocal(next.OP))
                {
                    if (next.OP == OPCode.GetLocal_0)
                    {
                        classToCheck = @class;
                        continue;
                    }

                    var local = (Local)next;
                    ASParameter parameter = constructor.Parameters[local.Register - 1];
                    structure.Add(parameter.Type.Name);
                }
                else
                {
                    if (next.OP == OPCode.FindPropStrict)
                    {
                        classToCheck = null;
                    }
                    else if (next.OP == OPCode.GetLex)
                    {
                        var getLex = (GetLexIns)next;
                        classToCheck = classToCheck.GetABC().GetFirstClass(getLex.TypeName.Name);
                    }
                    do
                    {
                        next = code[++i];
                        propertyName = null;
                        if (next.OP == OPCode.GetProperty)
                        {
                            getProperty = (GetPropertyIns)next;
                            propertyName = getProperty.PropertyName;
                        }
                        else if (next.OP == OPCode.CallProperty)
                        {
                            var callProperty = (CallPropertyIns)next;
                            propertyName = callProperty.PropertyName;
                        }
                    }
                    while (next.OP != OPCode.GetProperty && next.OP != OPCode.CallProperty);

                    if (TryGetTraitTypeName(classToCheck, propertyName, out string propertyTypeName) ||
                        TryGetTraitTypeName(classToCheck?.Instance, propertyName, out propertyTypeName))
                    {
                        structure.Add(propertyTypeName);
                    }
                }
            }
            if (structure.Contains("Array"))
            {
                // External array... impossible to check what value types are contained in this.
                return null;
            }
            return structure.ToArray();
        }
        private string[] GetOutgoingStructure(ASCode code, ASInstruction beforeReturn, int length)
        {
            var getLocalEndIndex = -1;
            int pushingEndIndex = code.IndexOf(beforeReturn);

            var structure = new string[length];
            ASInstance instance = Class.Instance;
            var pushedLocals = new Dictionary<int, int>();
            for (int i = pushingEndIndex - 1; i >= 0; i--)
            {
                ASInstruction instruction = code[i];
                if (instruction.OP == OPCode.GetProperty)
                {
                    ASClass classToCheck = Class;
                    var getProperty = (GetPropertyIns)instruction;
                    ASMultiname propertyName = getProperty.PropertyName;

                    ASInstruction previous = code[i - 1];
                    if (previous.OP == OPCode.GetLex)
                    {
                        var getLex = (GetLexIns)previous;
                        classToCheck = classToCheck.GetABC().GetFirstClass(getLex.TypeName.Name);
                    }

                    if (TryGetTraitTypeName(classToCheck, propertyName, out string propertyTypeName) ||
                        TryGetTraitTypeName(classToCheck.Instance, propertyName, out propertyTypeName))
                    {
                        structure[--length] = propertyTypeName;
                    }
                }
                else if (Local.IsGetLocal(instruction.OP) &&
                    instruction.OP != OPCode.GetLocal_0)
                {
                    var local = (Local)instruction;
                    pushedLocals.Add(local.Register, --length);
                    if (getLocalEndIndex == -1)
                    {
                        getLocalEndIndex = i;
                    }
                }
                if (length == 0) break;
            }
            for (int i = (getLocalEndIndex - 1); i >= 0; i--)
            {
                ASInstruction instruction = code[i];
                if (!Local.IsSetLocal(instruction.OP)) continue;

                var local = (Local)instruction;
                if (pushedLocals.TryGetValue(local.Register, out int structIndex))
                {
                    ASInstruction beforeSet = code[i - 1];
                    pushedLocals.Remove(local.Register);
                    switch (beforeSet.OP)
                    {
                        case OPCode.PushInt:
                        case OPCode.PushByte:
                        case OPCode.Convert_i:
                        structure[structIndex] = "int";
                        break;

                        case OPCode.Coerce_s:
                        case OPCode.PushString:
                        structure[structIndex] = "String";
                        break;

                        case OPCode.PushTrue:
                        case OPCode.PushFalse:
                        structure[structIndex] = "Boolean";
                        break;

                        default:
                        throw new Exception($"Don't know what this value type is, tell someone about this please.\r\nOP: {beforeSet.OP}");
                    }
                }
                if (pushedLocals.Count == 0) break;
            }
            return structure;
        }

        private string GetReadReturnTypeName(ASMultiname propertyName)
        {
            switch (propertyName.Name)
            {
                case "readString":
                return "String";

                case "readBoolean":
                return "Boolean";

                case "readByte":
                return "Byte";

                case "readDouble":
                return "Double";

                default:
                {
                    if (!HGame.IsValidIdentifier(propertyName.Name, true))
                    {
                        // Most likely: readInt
                        return "int";
                    }
                    return null;
                }
            }
        }
        private string GetStrictReturnTypeName(ASMultiname propertyName)
        {
            switch (propertyName.Name)
            {
                case "int":
                case "getTimer": return "int";
            }
            return null;
        }
        private bool TryGetTraitTypeName(ASContainer container, ASMultiname propertyName, out string propertyTypeName)
        {
            if (container == null)
            {
                propertyTypeName =
                    GetStrictReturnTypeName(propertyName);
            }
            else
            {
                ASTrait propertyTrait = container.GetTraits(
                    TraitKind.Slot, TraitKind.Constant, TraitKind.Getter)
                    .Where(t => t.QName == propertyName)
                    .FirstOrDefault();

                propertyTypeName = propertyTrait?.Type.Name;
            }
            return (propertyTypeName != null);
        }
        #endregion
    }
    public class MessageHasher : BinaryWriter
    {
        public bool IsSorting { get; set; }

        private readonly SortedDictionary<int, int> _ints;
        private readonly SortedDictionary<bool, int> _bools;
        private readonly SortedDictionary<byte, int> _bytes;
        private readonly SortedDictionary<string, int> _strings;

        public MessageHasher(bool isSorting)
            : base(new MemoryStream())
        {
            _ints = new SortedDictionary<int, int>();
            _bools = new SortedDictionary<bool, int>();
            _bytes = new SortedDictionary<byte, int>();
            _strings = new SortedDictionary<string, int>();

            IsSorting = isSorting;
        }

        public override void Write(int value)
        {
            WriteOrSort(_ints, base.Write, value);
        }
        public override void Write(bool value)
        {
            WriteOrSort(_bools, base.Write, value);
        }
        public override void Write(byte value)
        {
            WriteOrSort(_bytes, base.Write, value);
        }
        public override void Write(string value)
        {
            WriteOrSort(_strings, base.Write, value);
        }

        public void Write(ASTrait trait)
        {
            Write(trait.Id);
            Write(trait.QName);
            Write(trait.IsStatic);
            Write((byte)trait.Kind);
            Write((byte)trait.Attributes);
            switch (trait.Kind)
            {
                case TraitKind.Slot:
                case TraitKind.Constant:
                {
                    Write(trait.Type);
                    if (trait.Value != null)
                    {
                        Write(trait.ValueKind, trait.Value);
                    }
                    break;
                }
                case TraitKind.Method:
                case TraitKind.Getter:
                case TraitKind.Setter:
                {
                    Write(trait.Method);
                    break;
                }
            }
        }
        public void Write(ASMethod method)
        {
            Write(method.IsConstructor);
            if (!method.IsConstructor)
            {
                Write(method.ReturnType);
                if (method.Trait != null)
                {
                    Write(method.Trait.QName);
                }
            }

            Write(method.Parameters.Count);
            foreach (ASParameter parameter in method.Parameters)
            {
                Write(parameter.Type);
                if (!string.IsNullOrWhiteSpace(parameter.Name) &&
                    HGame.IsValidIdentifier(parameter.Name, true))
                {
                    Write(parameter.Name);
                }

                Write(parameter.IsOptional);
                if (parameter.IsOptional)
                {
                    Write((byte)parameter.ValueKind);
                    Write(parameter.ValueKind, parameter.Value);
                }
            }

            ASCode code = method.Body.ParseCode();
            foreach (KeyValuePair<OPCode, List<ASInstruction>> group in code.GetOPGroups())
            {
                Write((byte)group.Key);
                Write(group.Value.Count);
            }
        }
        public void Write(ASMultiname multiname)
        {
            if (multiname?.Kind == MultinameKind.TypeName)
            {
                Write(multiname.QName);
                Write(multiname.TypeIndices.Count);
                foreach (ASMultiname type in multiname.GetTypes())
                {
                    Write(type);
                }
            }
            else if (multiname == null ||
                HGame.IsValidIdentifier(multiname.Name, true))
            {
                Write(multiname?.Name ?? "*");
            }
        }
        public void Write(ConstantKind kind, object value)
        {
            Write((byte)kind);
            switch (kind)
            {
                case ConstantKind.Double:
                Write((double)value);
                break;

                case ConstantKind.Integer:
                Write((int)value);
                break;

                case ConstantKind.UInteger:
                Write((uint)value);
                break;

                case ConstantKind.String:
                Write((string)value);
                break;

                case ConstantKind.Null:
                Write("null");
                break;

                case ConstantKind.True:
                Write(true);
                break;

                case ConstantKind.False:
                Write(false);
                break;
            }
        }
        public void Write(ASContainer container, bool includeTraits)
        {
            Write(container.QName);
            Write(container.IsStatic);
            if (includeTraits)
            {
                Write(container.Traits.Count);
                container.Traits.ForEach(t => Write(t));
            }
        }

        public string GetHash()
        {
            Flush();
            using (var md5 = MD5.Create())
            {
                long curPos = BaseStream.Position;
                BaseStream.Position = 0;

                byte[] hashData = md5.ComputeHash(BaseStream);
                string hashAsHex = (BitConverter.ToString(hashData));

                BaseStream.Position = curPos;
                return hashAsHex.Replace("-", string.Empty).ToLower();
            }
        }
        public override void Flush()
        {
            WriteSorted(_ints, base.Write);
            WriteSorted(_bools, base.Write);
            WriteSorted(_bytes, base.Write);
            WriteSorted(_strings, base.Write);
        }

        private void WriteSorted<T>(IDictionary<T, int> storage, Action<T> writer)
        {
            foreach (KeyValuePair<T, int> storedPair in storage)
            {
                writer(storedPair.Key);
                base.Write(storedPair.Value);
            }
        }
        private void WriteOrSort<T>(IDictionary<T, int> storage, Action<T> writer, T value)
        {
            if (IsSorting)
            {
                if (storage.ContainsKey(value))
                {
                    storage[value]++;
                }
                else storage.Add(value, 1);
            }
            else writer(value);
        }
    }
    public class MessageReference
    {
        public bool IsStatic { get; set; }
        public bool IsAnonymous { get; set; }

        public int GroupCount { get; set; }

        public int ClassRank { get; set; }
        public int MethodRank { get; set; }
        public int InstructionRank { get; set; }

        public ASClass FromClass { get; set; }
        public ASMethod FromMethod { get; set; }
    }
}