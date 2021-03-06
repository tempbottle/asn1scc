﻿module AcnCreayeFromAntlr

open System.Linq
open System.Numerics
open Ast
open Antlr.Acn
open Antlr.Runtime.Tree
open Antlr.Runtime

open FsUtils
open AcnTypes




type PropOrLongReference =
    | Prop of AcnProperty
    | LRef of list<RelPath*LongReferenceKind*SrcLoc>

type SizePropAux =
    | SizeFixed of BigInteger
    | SizeNullTerminated 
    | SizeField of RelPath

let CreateLongField(t:ITree) = t.Children |> List.map(fun x -> x.Text) 

let GetLastChildLocation(t:ITree) = (t.Children |> List.rev).Head.Location

//Check ACN module name exists as an ASN.1 module
let CheckModuleName (modNameL:StringLoc) (r:Ast.AstRoot) =
    let extToThrow = SemanticError(modNameL.Location, (sprintf "No ASN.1 module is defined with name '%s'" modNameL.Value))
    CheckExists modNameL (r.Modules |> Seq.map(fun x -> x.Name)) extToThrow

let CheckTasName modName (tasNameL:StringLoc) (r:Ast.AstRoot)=
    let m = r.Modules |> Seq.find(fun x -> x.Name.Value = modName)
    let extToThrow = SemanticError(tasNameL.Location, (sprintf "No ASN.1 Type Assigment is defined with name '%s'" tasNameL.Value))
    CheckExists tasNameL (m.TypeAssignments |> Seq.map(fun x -> x.Name)) extToThrow



let rec CreateAcnAst(files:seq<ITree*string*array<IToken>>) (r:Ast.AstRoot) : AcnAst=  
    ITree.RegisterFiles(files|> Seq.map(fun (a,b,c) -> (a,b)))
    let constants = seq {
        for (f, s,_) in files do
            for m in f.Children do
                for c in m.GetChildrenByType(acnParser.CONSTANT) do
                    yield c } |> Seq.toList
    let constantNames = constants |> List.map(fun c -> c.GetChild(0).TextL)
    // check that all constant names are unique
    constantNames |> CheckForDuplicates 
    let constantValues = constants |> List.map (CreateNamedExpression (r, constantNames)) 
    let ast = {
                AcnAst.Constants = constantValues
                Types     = []
                Parameters = []
                References = []
                Files = files |> Seq.map(fun (a,b,c) -> (b,c)) |> Seq.toList
            }
    CheckCircularDependenciesInAcnConstants constants
    files |> Seq.map (fun (a,_,_)->a) |> Seq.toList |> Seq.fold(fun a f -> CreateAcnFile (files |> Seq.map (fun (a,_,_)->a) )f a r)    ast


and CheckCircularDependenciesInAcnConstants (constants : List<ITree>) =
    let HandleConstant (t:ITree) =
        let rec GetNamesFromExpr (t:ITree) =
            seq {
                match t.Type with
                | acnParser.INT                 -> ()
                | acnParser.UID                 -> yield t.TextL
                | acnParser.PLUS |acnParser.MINUS | acnParser.MULTIPLICATION | acnParser.DIVISION | acnParser.MODULO | acnParser.POWER_SYMBOL        -> 
                    yield! GetNamesFromExpr (t.GetChild(0))
                    yield! GetNamesFromExpr (t.GetChild(1))
                | acnParser.UNARY_MINUS         -> yield! GetNamesFromExpr (t.GetChild(0)) 
                | _                             -> raise(BugErrorException("CheckCircularDependenciesInAcnConstants.HandleConstant.GetNamesFromExpr Unsupported operator"))
                } |> Seq.toList
        (t.GetChild(0).TextL, GetNamesFromExpr (t.GetChild(1)))
    let constantsExpanded = constants |> List.map HandleConstant
    let independentConstants = constantsExpanded |> List.filter(fun (nm, lst) -> lst.IsEmpty ) |> List.map fst
    let dependentConstansts = constantsExpanded |> List.filter(fun (nm, lst) -> not (lst.IsEmpty) )
    let comparer (s1:StringLoc) (s2:StringLoc) = s1.Value = s2.Value
    let ExToThrow (cyclicDepds:List<StringLoc*List<StringLoc>>) = 
        match cyclicDepds with
        | []        -> raise(BugErrorException(""))
        | (x,_)::xs -> 
            let names = cyclicDepds |> Seq.map (fun (n,_) -> n.Value) |> Seq.StrJoin ", "
            SemanticError(x.Location, sprintf "Cyclic dependencies in ACN constants: %s" names)
    DoTopologicalSort2 independentConstants dependentConstansts comparer ExToThrow |> ignore

and CreateNamedExpression (r:Ast.AstRoot, st:List<StringLoc>) (t:ITree) : AcnConstant= 
    let name = t.GetChild(0).TextL
    // Constanst have global scope (i.e. within all modules) and should not conflict with any type or value assigment names
    for m in r.Modules do
        let names = m.TypeAssignments |> Seq.map(fun x -> x.Name)
        CheckUniqueName name names (fun n -> SemanticError(name.Location, (sprintf "ACN Constant '%s' conflicts with the Type Assignment defined in %s " name.Value (PrintLocation n.Location)) ))
        let names = m.ValueAssignments |> Seq.map(fun x -> x.Name)
        CheckUniqueName name names (fun n -> SemanticError(name.Location, (sprintf "ACN Constant '%s' conflicts with the Value Assignment defined in %s " name.Value (PrintLocation n.Location)) ))
    {AcnConstant.Name = t.GetChild(0).TextL;  Value = CreateExpression (r,st) (t.GetChild(1)) }

and CreateExpression (r:Ast.AstRoot, st:List<StringLoc>)  (t:ITree) = 
    match t.Type with
    | acnParser.INT | acnParser.UID -> IntegerExpr(CreateAcnIntegerConstant st t)
    | acnParser.PLUS                -> SumExpr(CreateExpression (r,st) (t.GetChild(0)), CreateExpression (r,st) (t.GetChild(1)))
    | acnParser.MINUS               -> MinExpr(CreateExpression (r,st) (t.GetChild(0)), CreateExpression (r,st) (t.GetChild(1)))
    | acnParser.MULTIPLICATION      -> MulExpr(CreateExpression (r,st) (t.GetChild(0)), CreateExpression (r,st) (t.GetChild(1)))
    | acnParser.DIVISION            -> DivExpr(CreateExpression (r,st) (t.GetChild(0)), CreateExpression (r,st) (t.GetChild(1)))
    | acnParser.MODULO              -> ModExpr(CreateExpression (r,st) (t.GetChild(0)), CreateExpression (r,st) (t.GetChild(1)))
    | acnParser.POWER_SYMBOL        -> PowExpr(CreateExpression (r,st) (t.GetChild(0)), CreateExpression (r,st) (t.GetChild(1)))
    | acnParser.UNARY_MINUS         -> UnMinExp(CreateExpression (r,st) (t.GetChild(0)))
    | _                             -> raise( BugErrorException("AcnCreayeFromAntlr::CreateExpression Unsupported operator"))

and CreateAcnIntegerConstant constantsSet (t:ITree) = 
    match t.Type with
    | acnParser.INT                 -> IntConst(t.BigIntL)
    | acnParser.UID                 -> 
        let extToThrow = SemanticError(t.Location, (sprintf "No ACN constant is defined with name '%s'" t.Text))
        CheckExists t.TextL constantsSet extToThrow
        RefConst(t.TextL)
    | _                             -> raise(BugErrorException("AcnCreayeFromAntlr::CreateAcnIntegerConstant"))


and CreateAcnFile (files:seq<ITree>) (t:ITree) (ast:AcnAst) (r:Ast.AstRoot) = 
    t.Children |> List.fold(fun a x -> CreateAcnModule files x a r) ast

and CreateAcnModule (files:seq<ITree>) (t:ITree) (ast:AcnAst)  (r:Ast.AstRoot) =
    //Check ACN module name exists as an ASN.1 module
    let modNameL = t.GetChildByType(acnParser.UID).TextL
    CheckModuleName modNameL r
    let tases = t.GetChildrenByType(acnParser.TYPE_ENCODING)
    
    //check for duplicate type assignments in the ACN module
    tases |> List.map(fun x -> x.GetChildByType(acnParser.UID).TextL) |> CheckForDuplicates

    tases |> List.fold(fun a x-> CreateAcnTypeAssigment files x modNameL.Value a r) ast

and CreateAcnTypeAssigment (files:seq<ITree>) (t:ITree) modName (ast:AcnAst) (r:Ast.AstRoot) =
    //Check that the ACN tas name exists also as an ASN.1 tas name
    let tasNameL = t.GetChildByType(acnParser.UID).TextL
    CheckTasName modName tasNameL r

    let absPath = [modName; tasNameL.Value]
    let encSpec = t.GetChildByType(acnParser.ENCODING_SPEC)
    let prms = match t.GetOptChild(acnParser.PARAM_LIST) with
               | None -> []
               | Some(paramList) -> 
                    let CreateParam (x:ITree) =
                        let prmName = x.GetChild(1).Text
                        let loc = x.GetChild(1).Location
                        let refs = encSpec.AllChildren |> List.filter(fun x -> x.Type = acnParser.LONG_FIELD && x.ChildCount=1) |> List.map(fun x -> x.GetChild(0).Text)
                        match refs |> Seq.tryFind(fun x -> x = prmName) with
                        | Some(_)   -> {AcnParameter.ModName=modName; TasName = tasNameL.Value; Name = prmName; Asn1Type=CreateAcnAsn1Type (x.GetChild(0)) r; Mode = DecodeMode; Location = loc}
                        | None      -> raise(SemanticError(loc, sprintf "unreferenced parameter '%s'" prmName))
                    paramList.Children |> List.map CreateParam
    let asn1Type = Ast.GetTypeByAbsPath absPath r

    CreateAcnType files encSpec asn1Type absPath RecordField (t.GetChildByType(acnParser.UID).Location) {ast with Parameters = ast.Parameters@prms} r


and CreateAcnAsn1Type (t:ITree) (r:Ast.AstRoot) =
    match t.Type with
    | acnParser.INTEGER         -> AcnAsn1Type.Integer
    | acnParser.BOOLEAN         -> AcnAsn1Type.Boolean
    | acnParser.NULL            -> AcnAsn1Type.NullType
    | acnParser.REFERENCED_TYPE -> 
        let mdName, tsName = 
            match t.Children with
            | first::[]             -> first.GetAncestor(acnParser.MODULE_DEF).GetChild(0).TextL,first.TextL
            | first::sec::[]        -> first.TextL,sec.TextL
            | _                     -> raise(BugErrorException("AcnCreayeFromAntlr::CreateAcnAsn1Type 1"))
        CheckModuleName mdName r
        CheckTasName mdName.Value tsName r
        AcnAsn1Type.RefTypeCon(mdName, tsName)
    | _                         -> raise(BugErrorException("AcnCreayeFromAntlr::CreateAcnAsn1Type 2"))


and CreateAcnType (files:seq<ITree>) (t:ITree) (asn1Type:Asn1Type) absPath implMode location (ast:AcnAst) (r:Ast.AstRoot)  =

    let props = match t.GetOptChild(acnParser.ENCODING_PROPERTIES) with
                | None              -> []
                | Some(propList)    -> propList.Children
    
    CheckConsistencyOfAsn1TypeWithAcnProperties t asn1Type absPath props ast r

    let propsWithRefs = 
        props |> List.map(fun x->
                            let rest = props |> List.filter(fun y -> not (System.Object.ReferenceEquals(y,x)))
                            HandleAcnProperty x rest asn1Type absPath ast r) 

    let props = propsWithRefs |> List.choose(fun x -> match x with Prop p -> Some p | _ -> None)
    let enumResetProps = GetEnumValuesResetInAcn t asn1Type |> List.map(fun (nm,vl) -> EnumeratorResetValue(nm,vl))
    let acnType = {AcnType.TypeID = absPath; ImpMode=implMode; Properties = props@enumResetProps; Location = location}
    let LRer_2_LongReference (relPat:RelPath, kind:LongReferenceKind, loc) =
        {LongReference.TypeID = absPath; LongRef=relPat; Kind = kind; Location = loc }
    let longReferences = propsWithRefs |> List.choose(fun x -> match x with LRef(lf) -> Some(lf|>List.map LRer_2_LongReference)|_ -> None) |> List.concat
    let ast0 = {ast with Types=ast.Types@[acnType]; References=ast.References@longReferences}
    
    let childrenEncSpec = match asn1Type.Kind with
                          | Enumerated(_)   -> None
                          | _               -> t.GetOptChild(acnParser.CHILDREN_ENC_SPEC)

    match childrenEncSpec with
    | None                  -> ast0
    | Some(childrenList)    -> 
        //check that ACN inserted fields are unique
        let newChildren = childrenList.Children |> Seq.filter(fun x -> x.Type = acnParser.CHILD_NEW) |> Seq.map(fun x -> x.GetChild(0).TextL)
        newChildren |> CheckForDuplicates

        childrenList.Children |>List.fold(fun a x -> CreateAcnChild files x  asn1Type absPath a r ) ast0


and GetParams (files:seq<ITree>) modName tasName  =
    let GetParamsAux (asn1File:ITree) =
        match  asn1File.Children |> Seq.tryFind(fun x -> x.GetChild(0).Text = modName)   with
        | None      -> []
        | Some(md)  ->
            match md.GetChildrenByType(acnParser.TYPE_ENCODING) |> Seq.tryFind(fun x -> x.GetChild(0).Text = tasName)   with
            | None  -> []
            | Some(tas) ->
                match tas.GetOptChild(acnParser.PARAM_LIST) with
                | None          -> []
                | Some(prmLst)  -> prmLst.Children |> List.map(fun p -> p.GetChild(1).Text)
    files |> Seq.toList |> List.map GetParamsAux |> List.collect(fun x -> x)

and CreateAcnChild (files:seq<ITree>) (t:ITree)  (asn1Parent: Asn1Type) absPath (ast:AcnAst) (r:Ast.AstRoot) : AcnAst = 
    let name  = 
        match t.GetOptChild(acnParser.LID) with
            | None          -> "#" 
            | Some(lid)     -> lid.Text

    let newAbsPath = absPath@[name]
    let loc = t.Location

    let CheckChild (children:list<ChildInfo>) checkFunc exc = 
        //Check that name exists within children
        match t.GetOptChild(acnParser.LID) with
        | None          -> raise(SemanticError(t.Location, "Component name missing"))
        | Some(lid)     -> 
            checkFunc (lid.TextL) (children |> Seq.map(fun x -> x.Name)) exc

    let GetArgumentList childTypeKind =
        match t.GetOptChild(acnParser.ARGUMENTS) with
        | None            -> []
        | Some(argList)   -> 
            //I have arguments ==> I have to be a ref type.
            let prmNames = match childTypeKind with
                            | ReferenceType(md,ts, _)  ->  GetParams files md.Value ts.Value
                            | _                        -> raise(SemanticError(t.Location, "Only reference types can have arguments" ))
            if argList.Children.Length <> prmNames.Length then
                raise(SemanticError(t.Location, sprintf "Expecting %d arguments" prmNames.Length ))
            let zipped = List.zip argList.Children prmNames
            zipped |> List.map(fun (a, prmName) -> {LongReference.TypeID = newAbsPath; LongRef = CreateLongField a; Kind = RefTypeArgument prmName; Location = GetLastChildLocation a })

    let Handle_ExistingChild children =
        //Check that name exists within children
        CheckChild children CheckExists (SemanticError(t.Location, (sprintf "'%s' Unknwon field name" name)))

        let asn1Child = children |> Seq.find(fun x -> x.Name.Value = name)
        let args= GetArgumentList asn1Child.Type.Kind
        CreateAcnType files (t.GetChildByType acnParser.ENCODING_SPEC) asn1Child.Type newAbsPath RecordField loc {ast with References = ast.References@ args} r

    match asn1Parent.Kind with
    | Sequence(children)    ->
        match t.Type with
        | acnParser.CHILD       ->            Handle_ExistingChild children
        | acnParser.CHILD_NEW   ->
            // check that the new child is not defined in ASN.1
            CheckChild children CheckNotExists (SemanticError(t.Location, (sprintf "'%s' is already defined in ASN.1" name)))
            
            let acnAsn1Type = CreateAcnAsn1Type (t.GetChild(1)) r
            let implMode = LocalVariable (acnAsn1Type)
            let asn1Type = {AcnAsn1Type2Asn1Type acnAsn1Type with Location = t.GetChild(1).Location}
            CreateAcnType files (t.GetChildByType acnParser.ENCODING_SPEC) asn1Type newAbsPath implMode loc ast r
        | _                             -> raise(BugErrorException("AcnCreayeFromAntlr::CreateAcnChild"))
    | Choice(children)      ->
        match t.Type with
        | acnParser.CHILD               -> Handle_ExistingChild children
        | acnParser.CHILD_NEW           -> raise(SemanticError(t.Location, "New fields can be inserted within sequences, not choices."))
        | _                             -> raise(BugErrorException("AcnCreayeFromAntlr::CreateAcnChild"))
    | SequenceOf(child)     ->
        match t.Type with
        | acnParser.CHILD       ->
            //Check that no name is provided
            match t.GetOptChild(acnParser.LID) with
            | None          -> ()
            | Some(lid)     -> raise(SemanticError(t.Location, sprintf "%s Unexpected field name" lid.Text))

            let args= GetArgumentList child.Kind
            CreateAcnType files (t.GetChildByType acnParser.ENCODING_SPEC) child newAbsPath RecordField loc {ast with References = ast.References@ args} r
        | acnParser.CHILD_NEW           -> raise(SemanticError(t.Location, "New fields can be inserted within sequences, not sequence ofs."))
        | _                             -> raise(BugErrorException("AcnCreayeFromAntlr::CreateAcnChild"))
    | _                     -> raise(SemanticError(t.Location, "Only Sequences, Choices and Sequence Ofs may contain child element specifications."))
        


and HandleAcnProperty(t:ITree) (rest:List<ITree>) (asn1Type: Asn1Type) absPath (ast:AcnAst) (r:Ast.AstRoot) : PropOrLongReference = 
//    CheckProperty t rest asn1Type absPath ast r
    let GetActualString (str:string) = 
        let strVal = str.Substring(1)
        strVal.Remove(strVal.Length-2).Replace("\r", "").Replace("\n", "").Replace("\t", "").Replace(" ", "")
    let constantNames = ast.Constants |> List.map(fun x -> x.Name)
    match t.Type with
    | acnParser.ENCODING    ->
        match t.GetChild(0).Type with
        | acnParser.POS_INT             -> Prop (Encoding PosInt)
        | acnParser.TWOSCOMPLEMENT      -> Prop (Encoding TwosComplement)
        | acnParser.BCD                 -> Prop (Encoding BCD)
        | acnParser.ASCII               -> Prop (Encoding Ascii)
        | acnParser.IEEE754_1985_32     -> Prop (Encoding IEEE754_32)
        | acnParser.IEEE754_1985_64     -> Prop (Encoding IEEE754_64)
        | _                             -> raise(BugErrorException(""))
    
    | acnParser.SIZE        ->
        match t.GetChild(0).Type with
        | acnParser.NULL_TERMINATED     -> Prop (SizeProperty sizeProperty.NullTerminated)
        | acnParser.INT | acnParser.UID -> Prop (SizeProperty (Fixed(CreateAcnIntegerConstant constantNames (t.GetChild(0)))))
        | acnParser.LONG_FIELD          -> LRef ( [CreateLongField(t.GetChild(0)), SizeDeterminant, GetLastChildLocation(t.GetChild(0))])
        | _                             -> raise(BugErrorException(""))
    
//    | acnParser.ADJUST                  -> Prop (Adjust(CreateAcnIntegerConstant constantNames (t.GetChild(0))))
    | acnParser.ALIGNTONEXT ->
        match t.GetChild(0).Type with 
        | acnParser.BYTE                -> Prop (Aligment NextByte)
        | acnParser.WORD                -> Prop (Aligment NextWord)
        | acnParser.DWORD               -> Prop (Aligment NextDWord)
        | _                             -> raise(BugErrorException(""))
   
    | acnParser.ENCODE_VALUES           -> Prop EncodeValues
    | acnParser.PRESENT_WHEN            -> 
        let CreateAcnPresenseCondition(t:ITree) = 
            let longField, kind = 
                match t.Type with
                | acnParser.LONG_FIELD  -> CreateLongField(t), PresenceBool 
                | acnParser.EQUAL       -> CreateLongField(t.GetChild(0)), PresenceInt (CreateAcnIntegerConstant constantNames (t.GetChild(1)))
                | acnParser.PRESENT_WHEN_STR_EQUAL ->
                    CreateLongField(t.GetChild(0)), PresenceStr (t.GetChild(1).Text.Replace("\"",""))
                | _                     -> raise(BugErrorException(""))
            longField, kind, GetLastChildLocation t
        LRef(t.Children |> List.map CreateAcnPresenseCondition)
    | acnParser.TRUE_VALUE              -> 
        let v = { StringLoc.Value = GetActualString(t.GetChild(0).Text); Location = t.GetChild(0).Location}
        Prop (BooleanEncoding(TrueValue(v)))
    | acnParser.FALSE_VALUE             -> 
        let v = { StringLoc.Value = GetActualString(t.GetChild(0).Text); Location = t.GetChild(0).Location}
        Prop (BooleanEncoding(FalseValue(v)))
    | acnParser.PATTERN                 -> 
        let v = { StringLoc.Value = GetActualString(t.GetChild(0).Text); Location = t.GetChild(0).Location}
        Prop (NullValue v)
    | acnParser.DETERMINANT             -> LRef( [CreateLongField(t.GetChild(0)),ChoiceDeteterminant, GetLastChildLocation(t.GetChild(0))])
    | acnParser.ENDIANNES               -> 
        match t.GetChild(0).Type with 
        | acnParser.BIG                 -> Prop (Endianness BigEndianness)
        | acnParser.LITTLE              -> Prop (Endianness LittleEndianness)
        | _                             -> raise(BugErrorException(""))
    | _                                 -> raise(BugErrorException(""))




and CheckConsistencyOfAsn1TypeWithAcnProperty asn1Kind (t:ITree) =
    match BalladerProperties |> Seq.exists(fun x -> x = t.Type) with
    | true  -> ()   //ballader propery, so it can be applied to any type
    | false ->
        //1. Is it allowed
        match (AllowedPropertiesPerType asn1Kind) |> Seq.exists(fun x -> x = t.Type) with
        | false     -> raise(SemanticError(t.Location, sprintf "Acn property '%s' cannot be applied here" t.Text))
        | true      -> ()
            
and CheckConsistencyOfAsn1TypeWithAcnProperties (t:ITree) asn1Type absPath (props:List<ITree>) (ast:AcnAst) (r:Ast.AstRoot) =
    //check each property against the asn1 type
    props |> Seq.iter(fun x -> CheckConsistencyOfAsn1TypeWithAcnProperty asn1Type.Kind x)
    //check for duplicate ACN properties
    props |> Seq.map(fun x -> x.TextL) |> CheckForDuplicates 

    //check than no mandatory properties are missing
    let propsNoBallader = props |> List.filter(fun x -> not(BalladerProperties |> List.contains x.Type ) )
    match propsNoBallader with
    | []    -> ()   // uper mode
    | x::xs -> 
        let errLoc = x.Location
        let mandProps = MandatoryAcnPropertiesPerType asn1Type.Kind 
        let missing = mandProps |> List.filter(fun x -> not(propsNoBallader |> List.exists(fun y -> y.Type=x) ))
        match missing with
        | []    -> ()
        | _     -> raise(SemanticError(errLoc, sprintf "missing properties : %s" (missing |> Seq.map PropID_to_Text |>Seq.StrJoin ", ") ))

    let CheckInt uperRange =
        let asn1Min, asn1Max =  match uperRange with
                                | Concrete(a,b)             -> a, b
                                | NegInf(b)                 -> MinInt(), b
                                | PosInf(a)                 -> a, MaxInt()
                                | Empty                     -> MaxInt(), MinInt()
                                | Full                      -> MinInt(), MaxInt()

        let sz = GetSizeProperty props ast.Constants
        let enc = GetEncodingPropery props
        match sz,enc with
        | None, None    -> ()
        | Some(_,l), None                               -> raise(SemanticError(l,"'encoding' propery missing"))
        | None, Some(_,l)                               -> raise(SemanticError(l,"'size' propery missing"))
        | Some(SizeField(_),l), Some(_)                 -> raise(SemanticError(l,"Expecting an Integer value or an ACN constant as value for the size property"))
        | Some(_), Some(IEEE754_32,l)                   -> raise(SemanticError(l,"Invalid encoding property value. Expecting 'pos-int', or 'twos-complement', or 'BCD' or 'ASCII'"))
        | Some(_), Some(IEEE754_64,l)                   -> raise(SemanticError(l,"Invalid encoding property value. Expecting 'pos-int', or 'twos-complement', or 'BCD' or 'ASCII'"))
        | Some(SizeNullTerminated, l), Some(PosInt,_)   -> raise(SemanticError(l,"Invalid combination of encoding and size properties"))
        | Some(SizeNullTerminated, l), Some(TwosComplement,_)   -> raise(SemanticError(l,"Invalid combination of encoding and size properties"))
        | Some(SizeNullTerminated, l), Some(Ascii,_)    -> () // all values are allowed
        | Some(SizeNullTerminated, l), Some(BCD,_)      -> 
            //only non negative values are allowed 
            if (asn1Min<0I) then
                raise(SemanticError(l, "The applied ACN encoding does not allow negative values which are supported by the corresponding ASN.1 type"))
        | Some(SizeFixed(nBits),l), Some (PosInt,_)     -> 
            if (asn1Min<0I) then
                raise(SemanticError(l, "The applied ACN encoding does not allow negative values which are supported by the corresponding ASN.1 type"))
            let maxAcn = BigInteger.Pow(2I, int(nBits))-1I 
            if (asn1Max>maxAcn) then
                raise(SemanticError(l, sprintf "The applied ACN encoding does not allow values larger than %A to be encoded, while the corresponding ASN.1 type allows values up to %A" maxAcn asn1Max))
            if nBits>WordSize() then    
                raise(SemanticError(l, sprintf "encoding size cannot be greater than %A" (WordSize())))
        | Some(SizeFixed(nBits),l), Some (TwosComplement,_)     -> 
            let minAcn = -BigInteger.Pow(2I, int(nBits-1I)) 
            let maxAcn = BigInteger.Pow(2I, int(nBits-1I))-1I 
            if (asn1Max>maxAcn) then
                raise(SemanticError(l, sprintf "The applied ACN encoding does not allow values larger than %A to be encoded, while the corresponding ASN.1 type allows values up to %A" maxAcn asn1Max))
            if (asn1Min<minAcn) then
                raise(SemanticError(l, sprintf "The applied ACN encoding does not allow values smaller than %A to be encoded, while the corresponding ASN.1 type allows values up to %A" maxAcn asn1Max))
            if nBits>WordSize() then    
                raise(SemanticError(l, sprintf "encoding size cannot be greater than %A" (WordSize())))
        | Some(SizeFixed(nBits),l), Some (Ascii,_)  when nBits%8I<>0I  -> raise(SemanticError(l,"size value should be multiple of 8"))
        | Some(SizeFixed(nBits),l), Some (Ascii,_)  -> 
            let digits = (nBits-8I)/8I
            let minAcn = -BigInteger.Pow(10I, int(digits))-1I 
            let maxAcn = BigInteger.Pow(10I, int(digits))-1I 
            if (asn1Max>maxAcn) then
                raise(SemanticError(l, sprintf "The applied ACN encoding does not allow values larger than %A to be encoded, while the corresponding ASN.1 type allows values up to %A" maxAcn asn1Max))
            if (asn1Min<minAcn) then
                raise(SemanticError(l, sprintf "The applied ACN encoding does not allow values smaller than %A to be encoded, while the corresponding ASN.1 type allows values up to %A" maxAcn asn1Max))
        | Some(SizeFixed(nBits),l), Some (BCD,_)  when nBits%4I<>0I  -> raise(SemanticError(l,"size value should be multiple of 4"))
        | Some(SizeFixed(nBits),l), Some (BCD,_)  -> 
            let digits = (nBits)/4I
            let maxAcn = BigInteger.Pow(10I, int(digits))-1I 
            if (asn1Max>maxAcn) then
                raise(SemanticError(l, sprintf "The applied ACN encoding does not allow values larger than %A to be encoded, while the corresponding ASN.1 type allows values up to %A" maxAcn asn1Max))
            if (asn1Min<0I) then
                raise(SemanticError(l, "The applied ACN encoding does not allow negative values which are supported by the corresponding ASN.1 type"))
    //Check present-when attribute
    CheckPresenceProperty t asn1Type absPath props ast r 

    // handle special cases
    match asn1Type.Kind with
    | Ast.Boolean -> 
        let hasTrue = props |> List.map(fun x -> x.Type) |> List.contains acnParser.TRUE_VALUE
        let hasFalse = props |> List.map(fun x -> x.Type) |> List.contains acnParser.FALSE_VALUE
        if hasTrue && hasFalse then
            raise(SemanticError(props.Head.Location, "only one of 'true-value' and 'false-value' can be specified"))

    | Ast.Integer -> 
        let uperRange = uPER.GetTypeUperRange asn1Type.Kind asn1Type.Constraints r
        CheckInt uperRange
    | Ast.Enumerated(nItems) -> 
        let uperRange = 
            match GetEncodeValuesProperty props with
            | None      -> Ast.uperRange.Concrete(0I, BigInteger (nItems.Length - 1))
            | Some(l)   -> 
                let encValues = 
                    match (GetEnumValuesResetInAcn t asn1Type) with
                    | []    ->  match nItems |> Seq.forall(fun x -> x._value.IsSome) with
                                | true  -> nItems |> List.mapi(fun idx itm -> GetValueAsInt itm._value.Value r)
                                | false -> nItems |> List.mapi(fun idx itm -> BigInteger idx)
                    | xs    -> xs |> List.map snd
                let minVal, maxVal = (encValues |> Seq.min ), (encValues |> Seq.max)
                Ast.uperRange.Concrete(minVal,maxVal)
        CheckInt uperRange
    | Ast.Real  ->
        match props |> Seq.tryFind(fun x -> x.Type = acnParser.ENCODING) with
        | None  -> ()
        | Some(enc) ->
            match enc.GetChild(0).Type with
            | acnParser.POS_INT | acnParser.TWOSCOMPLEMENT  | acnParser.BCD | acnParser.ASCII  -> 
                raise(SemanticError(enc.GetChild(0).Location, "Invalid encoding property value. Expecting 'IEEE754-1985-32' or 'IEEE754-1985-64'"))
            | _             -> ()
    | Ast.Choice(_)         ->
        CheckChoice t asn1Type absPath props ast r
    | Ast.IA5String | Ast.NumericString | Ast.OctetString | Ast.BitString | Ast.SequenceOf(_)  ->
        let uperRange = uPER.GetTypeUperRange asn1Type.Kind asn1Type.Constraints r
        match GetSizeProperty props ast.Constants with
        | None  -> ()
        | Some(SizeNullTerminated, l)   -> raise(SemanticError(l, "'size null-terminated' is supported only Integer types and when encoding is ASCII"))
        | Some(SizeFixed(nItems), l)    ->
            let asn1Min, asn1Max = uPER.GetSizebaleMinMax asn1Type.Kind asn1Type.Constraints r
            match asn1Min = asn1Max, asn1Max = nItems with
            | true, true        -> ()
            | true, false       -> raise(SemanticError(l, sprintf "size property value should be set to %A" asn1Max))
            | false, _          -> raise(SemanticError(l, sprintf "The size constraints of the ASN.1  allows multiple items (%A .. %A). Therefore, you should either remove the size property (in which case the size determinant will be encoded automatically exactly like uPER), or use a an Integer field as size determinant" asn1Min asn1Max))
        | Some (SizeField flds, l) -> ()
    | _           -> ()

and BalladerProperties = [acnParser.PRESENT_WHEN; acnParser.ALIGNTONEXT;]

and AllowedPropertiesPerType = function
    | Ast.Integer           -> [acnParser.ENCODING; acnParser.SIZE; acnParser.ENDIANNES]
    | Ast.Real              -> [acnParser.ENCODING; acnParser.ENDIANNES]
    | Ast.IA5String         -> [acnParser.SIZE]
    | Ast.NumericString     -> [acnParser.SIZE]
    | Ast.OctetString       -> [acnParser.SIZE]
    | Ast.NullType          -> [acnParser.PATTERN]
    | Ast.BitString         -> [acnParser.SIZE]
    | Ast.Boolean           -> [acnParser.TRUE_VALUE; acnParser.FALSE_VALUE]
    | Ast.Enumerated(_)     -> [acnParser.ENCODING; acnParser.SIZE; acnParser.ENCODE_VALUES; acnParser.ENDIANNES]
    | Ast.SequenceOf(_)     -> [acnParser.SIZE]
    | Ast.Sequence(_)       -> []
    | Ast.Choice(_)         -> [acnParser.DETERMINANT]
    | Ast.ReferenceType(_)  -> []


and MandatoryAcnPropertiesPerType asn1Kind : List<int> =
    match asn1Kind with
    | Ast.Integer           -> [acnParser.ENCODING; acnParser.SIZE; ]   //ADJUST = 0, ENDIANNES=big
    | Ast.Real              -> [acnParser.ENCODING; ]   //ENDIANNES=big
    | Ast.IA5String         -> [acnParser.SIZE]     // pointing to a field
    | Ast.NumericString     -> [acnParser.SIZE]     // pointing to a field
    | Ast.OctetString       -> [acnParser.SIZE]     // pointing to a field
    | Ast.NullType          -> []                   // pattern = ''
    | Ast.BitString         -> [acnParser.SIZE]     // pointing to a field
    | Ast.Boolean           -> []                   // true-value = '1'B
    | Ast.Enumerated(_)     -> [acnParser.ENCODING; acnParser.SIZE; ]
    | Ast.SequenceOf(_)     -> [acnParser.SIZE]     // pointing to a field
    | Ast.Sequence(_)       -> []
    | Ast.Choice(_)         -> []
    | Ast.ReferenceType(_)  -> []
    
and PropID_to_Text = function
    | acnParser.PRESENT_WHEN    -> "present-when"
    | acnParser.ALIGNTONEXT     -> "align-to-next"
    | acnParser.ENCODING        -> "encoding"
    | acnParser.SIZE            -> "size"
    | acnParser.ENDIANNES       -> "endianness"
    | acnParser.PATTERN         -> "pattern"
    | acnParser.TRUE_VALUE      -> "true-value"
    | acnParser.FALSE_VALUE     -> "false-value"
    | acnParser.ENCODE_VALUES   -> "encode-values"
    | acnParser.DETERMINANT     -> "determinant"
    | _                         -> raise(BugErrorException "")



and GetSizeProperty (props:List<ITree>) (constants:list<AcnConstant>) =
    match props |> Seq.tryFind(fun x -> x.Type = acnParser.SIZE) with
    | None      -> None
    | Some(sz)  -> 
        let sizeChild = sz.GetChild(0)
        match sizeChild.Type with
        | acnParser.NULL_TERMINATED     -> Some(SizeNullTerminated, sizeChild.Location)
        | acnParser.LONG_FIELD          ->
            let fields = sizeChild.Children |> List.map(fun x -> x.TextL)
            match fields with
            | []    -> raise(BugErrorException "")
            | x::_  -> 
                let flds = fields |> List.map(fun x -> x.Value)
                Some (SizeField flds, x.Location)
        | acnParser.INT                 -> Some(SizeFixed sizeChild.BigInt, sizeChild.Location)
        | acnParser.UID                 -> 
            let constant = RefConst sizeChild.TextL
            let nVal = EvaluateConstant constants constant
            Some(SizeFixed nVal, sizeChild.Location)
        | _                         -> raise(BugErrorException "GetSizePropertyValue")




and GetEncodingPropery (props:List<ITree>) =
    match props |> Seq.tryFind(fun x -> x.Type = acnParser.ENCODING) with
    | None      -> None
    | Some(enc)  -> 
        let loc = enc.GetChild(0).Location
        match enc.GetChild(0).Type with
        | acnParser.POS_INT             -> Some (PosInt, loc)
        | acnParser.TWOSCOMPLEMENT      -> Some (TwosComplement, loc)
        | acnParser.BCD                 -> Some (BCD, loc)
        | acnParser.ASCII               -> Some (Ascii, loc)
        | acnParser.IEEE754_1985_32     -> Some (IEEE754_32, loc)
        | acnParser.IEEE754_1985_64     -> Some (IEEE754_64, loc)
        | _                             -> raise(BugErrorException "GetEncodingPropery")
        
and GetEncodeValuesProperty (props:List<ITree>) =
    match props |> Seq.tryFind(fun x -> x.Type = acnParser.ENCODE_VALUES) with
    | None          -> None
    | Some(enc)     -> Some(enc.Location)
        


and GetEnumValuesResetInAcn (t:ITree) (asn1Type:Asn1Type)  =
    match asn1Type.Kind with
    | Ast.Enumerated(enumItems) -> 
        match t.GetOptChild(acnParser.CHILDREN_ENC_SPEC) with
        | None                  -> []
        | Some(childrenList)    -> 
            let errLoc = t.Location
            let enmChildren = 
                childrenList.Children |> 
                List.map(fun x -> match x.Type with
                                    |acnParser.CHILD -> 
                                    let name = x.GetChild(0).TextL
                                    let errLoc = x.GetChild(0).Location
                                    let chEncSpec = x.GetChildByType(acnParser.ENCODING_SPEC) 
                                    let encValue = match chEncSpec.GetOptChild acnParser.ENCODING_PROPERTIES with
                                                    | None -> raise(SemanticError(errLoc, "Expecting integer value"))
                                                    | Some(propLst)    ->
                                                        match propLst.Children with
                                                        | []        -> raise(SemanticError(errLoc, "Expecting integer value"))
                                                        | prop::_   -> match prop.Type with
                                                                        | acnParser.INT -> prop.BigIntL
                                                                        | _             -> raise(SemanticError(errLoc, "Expecting integer value"))
                                    ( name, encValue)
                                    | _  -> raise(SemanticError(x.Location, "Expecting an enumerated name")) )

            // 1 make sure that the two lists match
            let enmList1 = enumItems |> List.map(fun x -> x.Name) 
            let enmList2 = enmChildren |> List.map fst 
            CompareLists enmList1 enmList2 (fun x -> SemanticError(x.Location, sprintf "Unexpected value '%s' " x.Value))
            CompareLists enmList2 enmList1 (fun x -> SemanticError(errLoc, sprintf "Missing value '%s' " x.Value))
            //2 make sure that there is no duplicate value
            enmChildren |> List.map snd |> CheckForDuplicates
            let extraProps = enmChildren |> List.map(fun (nm, vl) -> (nm.Value, vl.Value))
            extraProps
    | _                         -> []


and CheckPresenceProperty (t:ITree) asn1Type absPath (props:List<ITree>) (ast:AcnAst) (r:Ast.AstRoot) =
    match props |> Seq.tryFind(fun x -> x.Type = acnParser.PRESENT_WHEN) with
    | None          -> ()
    | Some(prCon)     -> 
        // there is a present-when attribute, so let us check the ASN.1 parent
        let loc =   prCon.Location
        let conditions = prCon.Children
        let parPath = absPath |> List.rev |> List.tail |> List.rev
        let chName =  absPath |> List.rev |> List.head
        let ExcToThrow = SemanticError(loc, "'present-property can appear only within sequence components or choice alternatives")
        let parent = Ast.GetTypeByAbsPathEx parPath r ExcToThrow
        match parent.Kind with
        | Sequence(children)    -> 
            match conditions with
            | []        -> raise(BugErrorException "")
            | cond::[]  ->
                    match cond.Type with
                    | acnParser.LONG_FIELD  -> 
                        let child = children |> Seq.find(fun x -> x.Name.Value = chName)
                        match child.Optionality with
                        | None      
                        | Some(AlwaysAbsent)
                        | Some(AlwaysPresent)               -> raise(SemanticError(loc,"'present-when' attribute can be applied to optional components only"))
                        | Some(Optional) | Some(Default(_)) -> ()
                    | acnParser.EQUAL                  
                    | acnParser.PRESENT_WHEN_STR_EQUAL -> raise(SemanticError(loc,"the form 'present-when fld = value' can be applied only to choice alternatives"))
                    | _                     -> raise(BugErrorException(""))
            | _         ->  raise(SemanticError(loc,"Only one boolean condition can be applied"))
        | Choice(children)      -> ()
        | _                     -> raise ExcToThrow


and CheckChoice (t:ITree) asn1Type absPath (props:List<ITree>) (ast:AcnAst) (r:Ast.AstRoot) =
    let CreatePresenceCondition (t:ITree) =
        let constantNames = ast.Constants |> List.map(fun x -> x.Name)
        match t.Type with
        | acnParser.LONG_FIELD  -> raise(SemanticError(t.Location,"the form 'present-when boolFld' can be applied only to sequence components."))
        | acnParser.EQUAL       -> 
            let con1 = CreateAcnIntegerConstant constantNames (t.GetChild(1)) |> EvaluateConstant ast.Constants
            let con1ToUse = IntConst (IntLoc.ByValue con1)
            CreateLongField(t.GetChild(0)), PresenceInt con1ToUse
        | acnParser.PRESENT_WHEN_STR_EQUAL ->
            CreateLongField(t.GetChild(0)), PresenceStr (t.GetChild(1).Text.Replace("\"",""))
        | _                     -> raise(BugErrorException(""))

    let GetPresenceConditions (t:ITree) =
        match t.Type with
        | acnParser.CHILD   | acnParser.CHILD_NEW    -> 
            let encSpec = t.GetChildByType(acnParser.ENCODING_SPEC)
            let presentWhenConditions = 
                match encSpec.GetOptChild(acnParser.ENCODING_PROPERTIES) with
                | Some(encProps)    -> encProps.Children |> 
                                       List.filter(fun x -> x.Type = acnParser.PRESENT_WHEN) |> 
                                       List.collect(fun x -> x.Children) |>
                                       List.map CreatePresenceCondition |>
                                       List.sortBy(fun (lfield,_) -> lfield)
                | None              -> []
            presentWhenConditions
        | _         -> raise(BugErrorException "")
    match asn1Type.Kind with
    | Choice(children)      -> 
        match t.GetOptChild(acnParser.CHILDREN_ENC_SPEC) with
        | None                  -> ()
        | Some(childrenList)    -> 
            let loc = t.Location
            let childrenPresenceCods = childrenList.Children |> List.map GetPresenceConditions
            let firstChild = childrenPresenceCods.Head
            let exToThrow = SemanticError(loc, "Invalid presence-when attribute usage. The same type of conditions should be applied to all choice alternatives")
            let cmprConditionsWithFirstOne (curChild :List<List<string>*LongReferenceKind>) =
                let cmprCondition ((absPath1, presKind1):List<string>*LongReferenceKind) ((absPath2, presKind2):List<string>*LongReferenceKind) = 
                    if (absPath1 <> absPath2) then
                        raise exToThrow
                    match presKind1, presKind2 with
                    | PresenceBool, PresenceBool    -> ()
                    | PresenceInt(v1), PresenceInt(v2)  when v1=v2  -> raise exToThrow
                    | PresenceInt(v1), PresenceInt(v2)  -> ()
                    | PresenceStr(v1), PresenceStr(v2)  when v1=v2  -> raise exToThrow
                    | PresenceStr(v1), PresenceStr(v2)  -> ()
                    | _                                 -> raise exToThrow
                
                if firstChild.Length <> curChild.Length then
                    raise exToThrow
                List.zip firstChild curChild |> Seq.iter(fun (c1,c2) -> cmprCondition c1 c2)
            childrenPresenceCods.Tail |> Seq.iter cmprConditionsWithFirstOne
    | _                     -> ()
