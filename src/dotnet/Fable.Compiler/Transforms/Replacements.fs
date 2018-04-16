module Fable.Transforms.Replacements

open System.Collections.Generic
open Microsoft.FSharp.Compiler.SourceCodeServices
open Fable
open Fable.AST
open Fable.AST.Fable
open Patterns

type Context = Fable.Transforms.FSharp2Fable.Context
type ICompiler = Fable.Transforms.FSharp2Fable.IFableCompiler

type Helper =
    static member ConstructorCall(consExpr: Expr, returnType: Type, args: Expr list,
                                  ?argTypes: Type list, ?loc: SourceLocation) =
        Operation(Call(ConstructorCall consExpr, argInfo None args argTypes), returnType, loc)

    static member InstanceCall(callee: Expr, memb: string, returnType: Type, args: Expr list,
                               ?argTypes: Type list, ?loc: SourceLocation) =
        let kind = makeStrConst memb |> Some |> InstanceCall
        Operation(Call(kind, argInfo (Some callee) args argTypes), returnType, loc)

    static member Application(callee: Expr, returnType: Type, args: Expr list,
                               ?argTypes: Type list, ?loc: SourceLocation) =
        Operation(Call(InstanceCall None, argInfo (Some callee) args argTypes), returnType, loc)

    static member CoreCall(coreModule: string, coreMember: string, returnType: Type, args: Expr list,
                           ?argTypes: Type list, ?thisArg: Expr, ?isConstructor: bool, ?loc: SourceLocation) =
        let info = argInfo thisArg args argTypes
        let funcExpr = Import(coreMember, coreModule, CoreLib, Any)
        match isConstructor with
        | Some true -> Operation(Call(ConstructorCall funcExpr, info), returnType, loc)
        | _ -> Operation(Call(StaticCall funcExpr, info), returnType, loc)

    static member GlobalCall(ident: string, returnType: Type, args: Expr list,
                             ?argTypes: Type list, ?thisArg: Expr, ?memb: string, ?loc: SourceLocation) =
        let funcExpr =
            match memb with
            | Some m -> get None Any (makeIdentExpr ident) m
            | None -> makeIdentExpr ident
        let info = argInfo thisArg args argTypes
        Operation(Call(StaticCall funcExpr, info), returnType, loc)

    static member GlobalIdent(ident: string, memb: string, typ: Type, ?loc: SourceLocation) =
        get loc typ (makeIdentExpr ident) memb

module private Helpers =
    let resolveArgTypes argTypes genArgs =
        argTypes |> List.map (function
            | GenericParam name as t ->
                genArgs |> List.tryPick (fun (name2,t) ->
                    if name = name2 then Some t else None)
                |> Option.defaultValue t
            | t -> t)

    let emitJs r t args macro =
        let info = argInfo None args None
        Operation(Emit(macro, Some info), t, r)

    let objExpr t kvs =
        let kvs = List.map (fun (k,v) -> k, v, ObjectValue) kvs
        ObjectExpr(kvs, t, None)

    let add left right =
        Operation(BinaryOperation(BinaryPlus, left, right), left.Type, None)

    let eq left right =
        Operation(BinaryOperation(BinaryEqualStrict, left, right), Boolean, None)

    let neq left right =
        Operation(BinaryOperation(BinaryUnequalStrict, left, right), Boolean, None)

    let isNull expr =
        Operation(BinaryOperation(BinaryEqual, expr, Value(Null Any)), Boolean, None)

    let error msg =
        Helper.ConstructorCall(makeIdentExpr "Error", Any, [msg])

    let s txt = Value(StringConstant txt)

    let firstGenArg (com: ICompiler) r (genArgs: (string * Type) list) =
        List.tryHead genArgs
        |> Option.map snd
        |> Option.defaultWith (fun () ->
            "Couldn't find any generic argument" |> addError com r
            Any)

    let defaultof (t: Type) =
        match t with
        | Number _ -> makeIntConst 0
        | Boolean -> makeBoolConst false
        | _ -> Null t |> Value

open Helpers

type BuiltinType =
    | BclGuid
    | BclTimeSpan
    | BclDateTime
    | BclDateTimeOffset
    | BclInt64
    | BclUInt64
    | BclBigInt
    | FSharpSet
    | FSharpMap

let (|Builtin|_|) = function
    | DeclaredType(ent,_) ->
        match ent.TryFullName with
        | Some Types.guid -> Some BclGuid
        | Some Types.timespan -> Some BclTimeSpan
        | Some Types.datetime -> Some BclDateTime
        | Some Types.datetimeOffset -> Some BclDateTimeOffset
        | Some "System.Int64" -> Some BclInt64
        | Some "System.UInt64" -> Some BclUInt64
        | Some (Naming.StartsWith "Microsoft.FSharp.Core.int64" _) -> Some BclInt64
        | Some "System.Numerics.BigInteger" -> Some BclBigInt
        | Some "Microsoft.FSharp.Collections.FSharpSet" -> Some FSharpSet
        | Some "Microsoft.FSharp.Collections.FSharpMap" -> Some FSharpMap
        | _ -> None
    | _ -> None

let (|Integer|Float|) = function
    | Int8 | UInt8 | Int16 | UInt16 | Int32 | UInt32 -> Integer
    | Float32 | Float64 | Decimal -> Float

let (|Nameof|_|) = function
    | IdentExpr ident -> Some ident.Name
    | Get(_, ExprGet(Value(StringConstant prop)), _, _) -> Some prop
    | _ -> None

let (|ReplaceName|_|) (namesAndReplacements: (string*string) list) name =
    namesAndReplacements |> List.tryPick (fun (name2, replacement) ->
        if name2 = name then Some replacement else None)

let inline (|ExprType|) (e: Expr) = e.Type

let (|EntFullName|_|) (typ: Type) =
    match typ with
    | DeclaredType(ent, _) -> Some ent.FullName
    | _ -> None

let (|ListLiteral|_|) e =
    let rec untail t acc = function
        | Value(NewList(None, _)) -> Some(List.rev acc, t)
        | Value(NewList(Some(head, tail), _)) -> untail t (head::acc) tail
        | _ -> None
    match e with
    | Value(NewList(None, t)) -> Some([], t)
    | Value(NewList(Some(head, tail), t)) -> untail t [head] tail
    | _ -> None

let coreModFor = function
    | BclGuid -> "String"
    | BclDateTime -> "Date"
    | BclDateTimeOffset -> "DateOffset"
    | BclInt64 -> "Long"
    | BclUInt64 -> "Long"
    | BclBigInt -> "BigInt"
    | BclTimeSpan -> "Int32"
    | FSharpSet -> "Set"
    | FSharpMap -> "Map"

let makeLongInt t signed (x: uint64) =
    let lowBits = NumberConstant (float (uint32 x), Float64)
    let highBits = NumberConstant (float (x >>> 32), Float64)
    let unsigned = BoolConstant (not signed)
    let args = [Value lowBits; Value highBits; Value unsigned]
    Helper.CoreCall("Long", "fromBits", t, args)

let makeFloat32 (x: float32) =
    Helper.GlobalCall("Math", Number Float32, [NumberConstant (float x, Float32) |> Value], memb="fround")

let makeTypeConst (typ: Type) (value: obj) =
    match typ, value with
    // Long Integer types
    | Builtin BclInt64, (:? int64 as x) -> makeLongInt typ true (uint64 x)
    | Builtin BclUInt64, (:? uint64 as x) -> makeLongInt typ false x
    // Decimal type
    | Number Decimal, (:? decimal as x) -> makeDecConst x
    // Short Float type
    | Number Float32, (:? float32 as x) -> makeFloat32 x
    | Boolean, (:? bool as x) -> BoolConstant x |> Value
    | String, (:? string as x) -> StringConstant x |> Value
    | Char, (:? char as x) -> StringConstant (string x) |> Value
    // Integer types
    | Number UInt8, (:? byte as x) -> NumberConstant (float x, UInt8) |> Value
    | Number Int8, (:? sbyte as x) -> NumberConstant (float x, Int8) |> Value
    | Number Int16, (:? int16 as x) -> NumberConstant (float x, Int16) |> Value
    | Number UInt16, (:? uint16 as x) -> NumberConstant (float x, UInt16) |> Value
    | Number Int32, (:? int as x) -> NumberConstant (float x, Int32) |> Value
    | Number UInt32, (:? uint32 as x) -> NumberConstant (float x, UInt32) |> Value
    // Float types
    | Number Float64, (:? float as x) -> NumberConstant (float x, Float64) |> Value
    // Enums
    | EnumType _, (:? int64)
    | EnumType _, (:? uint64) -> failwith "int64 enums are not supported"
    | EnumType(_, name), (:? byte as x) -> Enum(NumberEnum(int x), name) |> Value
    | EnumType(_, name), (:? sbyte as x) -> Enum(NumberEnum(int x), name) |> Value
    | EnumType(_, name), (:? int16 as x) -> Enum(NumberEnum(int x), name) |> Value
    | EnumType(_, name), (:? uint16 as x) -> Enum(NumberEnum(int x), name) |> Value
    | EnumType(_, name), (:? int as x) -> Enum(NumberEnum(int x), name) |> Value
    | EnumType(_, name), (:? uint32 as x) -> Enum(NumberEnum(int x), name) |> Value
    // TODO: Regex
    | Unit, _ -> UnitConstant |> Value
    // Arrays with small data type (ushort, byte) are represented
    // in F# AST as BasicPatterns.Const
    | Array (Number kind), (:? (byte[]) as arr) ->
        let values = arr |> Array.map (fun x -> NumberConstant (float x, kind) |> Value) |> Seq.toList
        NewArray (ArrayValues values, Number kind) |> Value
    | Array (Number kind), (:? (uint16[]) as arr) ->
        let values = arr |> Array.map (fun x -> NumberConstant (float x, kind) |> Value) |> Seq.toList
        NewArray (ArrayValues values, Number kind) |> Value
    | _ -> failwithf "Unexpected type %A, literal %O" typ value

let rec makeTypeTest com range expr (typ: Type) =
    let jsTypeof (primitiveType: string) expr =
        let typof = makeUnOp None String expr UnaryTypeof
        makeEqOp range typof (makeStrConst primitiveType) BinaryEqualStrict
    let jsInstanceof (cons: Expr) expr =
        makeBinOp range Boolean expr cons BinaryInstanceOf
    match typ with
    | Any -> makeBoolConst true
    | Unit -> makeEqOp range expr (Value <| Null Any) BinaryEqual
    | Boolean -> jsTypeof "boolean" expr
    | Char | String _ -> jsTypeof "string" expr
    | FunctionType _ -> jsTypeof "function" expr
    | Number _ | EnumType _ -> jsTypeof "number" expr
    | Regex ->
        jsInstanceof (makeIdentExpr "RegExp") expr
    | Builtin (BclInt64 | BclUInt64) ->
        jsInstanceof (makeCoreRef Any "Long" "default") expr
    | Builtin BclBigInt ->
        jsInstanceof (makeCoreRef Any "BigInt" "default") expr
    | Array _ | Tuple _ | List _ ->
        Helper.CoreCall("Util", "isArray", Boolean, [expr], ?loc=range)
    | DeclaredType (ent, _) ->
        match ent.TryFullName with
        | Some "System.IDisposable" ->
            Helper.CoreCall("Util", "isDisposable", Boolean, [expr], ?loc=range)
        | _ ->
            if ent.IsClass
            then jsInstanceof (FSharp2Fable.Util.entityRef com ent) expr
            else "Cannot type test interfaces, records or unions"
                 |> addErrorAndReturnNull com range
    | Option _ | GenericParam _ | ErasedUnion _ ->
        "Cannot type test options, generic parameters or erased unions"
        |> addErrorAndReturnNull com range

let createAtom (value: Expr) =
    let typ = value.Type
    Helper.CoreCall("Util", "createAtom", typ, [value], [typ])

let toChar (sourceType: Type) (args: Expr list) =
    match sourceType with
    | Char
    | String -> args.Head
    | _ -> Helper.GlobalCall("String", Char, args, memb="fromCharCode")

let toString (sourceType: Type) (args: Expr list) =
    match sourceType with
    | Char | String -> args.Head
    | Unit | Boolean | Array _ | Tuple _ | FunctionType _ | EnumType _ ->
        Helper.GlobalCall("String", String, args)
    | Builtin (BclInt64 | BclUInt64) -> Helper.CoreCall("Long", "toString", String, args)
    | Number Int16 -> Helper.CoreCall("Util", "int16ToString", String, args)
    | Number Int32 -> Helper.CoreCall("Util", "int32ToString", String, args)
    | Number _ -> Helper.InstanceCall(args.Head, "toString", String, args.Tail)
    | _ -> Helper.CoreCall("Util", "toString", String, args)

let toFloat (sourceType: Type) targetType (args: Expr list) =
    match sourceType with
    | String ->
        Helper.CoreCall("Double", "parse", Number Float64, args)
    | Builtin (BclInt64 | BclUInt64) ->
        Helper.CoreCall("Long", "toNumber", Number Float64, args)
    | Builtin BclBigInt ->
        let meth = match targetType with
                    | Number Float32 -> "toSingle"
                    | Number Float64 -> "toDouble"
                    | Number Decimal -> "toDecimal"
                    | _ -> failwith "Unexpected BigInt conversion"
        Helper.CoreCall("BigInt", meth, Number Float64, args)
    | _ -> args.Head

// Apparently ~~ is faster than Math.floor (see https://coderwall.com/p/9b6ksa/is-faster-than-math-floor)
let fastIntFloor expr =
    let inner = makeUnOp None Any expr UnaryNotBitwise
    makeUnOp None (Number Int32) inner UnaryNotBitwise

let toInt (round: bool) (sourceType: Type) targetType (args: Expr list) =
    let kindIndex t =             //         0   1   2   3   4   5   6   7   8   9  10  11
        match t with              //         i8 i16 i32 i64  u8 u16 u32 u64 f32 f64 dec big
        | Number Int8 -> 0        //  0 i8   -   -   -   -   +   +   +   +   -   -   -   +
        | Number Int16 -> 1       //  1 i16  +   -   -   -   +   +   +   +   -   -   -   +
        | Number Int32 -> 2       //  2 i32  +   +   -   -   +   +   +   +   -   -   -   +
        | Builtin BclInt64 -> 3   //  3 i64  +   +   +   -   +   +   +   +   -   -   -   +
        | Number UInt8 -> 4       //  4 u8   +   +   +   +   -   -   -   -   -   -   -   +
        | Number UInt16 -> 5      //  5 u16  +   +   +   +   +   -   -   -   -   -   -   +
        | Number UInt32 -> 6      //  6 u32  +   +   +   +   +   +   -   -   -   -   -   +
        | Builtin BclUInt64 -> 7  //  7 u64  +   +   +   +   +   +   +   -   -   -   -   +
        | Number Float32 -> 8     //  8 f32  +   +   +   +   +   +   +   +   -   -   -   +
        | Number Float64 -> 9     //  9 f64  +   +   +   +   +   +   +   +   -   -   -   +
        | Number Decimal -> 10    // 10 dec  +   +   +   +   +   +   +   +   -   -   -   +
        | Builtin BclBigInt -> 11 // 11 big  +   +   +   +   +   +   +   +   +   +   +   -
        | _ -> failwith "Unexpected non-number type"
    let needToCast typeFrom typeTo =
        let v = kindIndex typeFrom // argument type (vertical)
        let h = kindIndex typeTo   // return type (horizontal)
        ((v > h) || (v < 4 && h > 3)) && (h < 8) || (h <> v && (h = 11 || v = 11))
    let emitLong unsigned (args: Expr list) =
        match sourceType with
        | Builtin (BclInt64|BclUInt64) -> Helper.CoreCall("Long", "fromValue", targetType, args)
        | _ -> Helper.CoreCall("Long", "fromNumber", targetType, args@[makeBoolConst unsigned])
    let emitBigInt (args: Expr list) =
        match sourceType with
        | Builtin (BclInt64|BclUInt64) -> Helper.CoreCall("BigInt", "fromInt64", targetType, args)
        | _ -> Helper.CoreCall("BigInt", "fromInt32", targetType, args)
    let emitCast typeTo args =
        match typeTo with
        | Builtin BclBigInt -> emitBigInt args
        | Builtin BclUInt64 -> emitLong true args
        | Builtin BclInt64 -> emitLong false args
        | Number Int8 -> emitJs None targetType args "($0 + 0x80 & 0xFF) - 0x80"
        | Number Int16 -> emitJs None targetType args "($0 + 0x8000 & 0xFFFF) - 0x8000"
        | Number Int32 -> fastIntFloor args.Head
        | Number UInt8 -> emitJs None targetType args "$0 & 0xFF"
        | Number UInt16 -> emitJs None targetType args "$0 & 0xFFFF"
        | Number UInt32 -> emitJs None targetType args "$0 >>> 0"
        // TODO: Use Cast(args.Head, targetType) for these cases (more below)?
        | Number Float32 -> args.Head
        | Number Float64 -> args.Head
        | Number Decimal -> args.Head
        | _ -> failwith "Unexpected non-number type"
    let castBigIntMethod typeTo =
        match typeTo with
        | Builtin BclBigInt -> failwith "Unexpected conversion"
        | Number Int8 -> "toSByte"
        | Number Int16 -> "toInt16"
        | Number Int32 -> "toInt32"
        | Builtin BclInt64 -> "toInt64"
        | Number UInt8 -> "toByte"
        | Number UInt16 -> "toUInt16"
        | Number UInt32 -> "toUInt32"
        | Builtin BclUInt64 -> "toUInt64"
        | Number Float32 -> "toSingle"
        | Number Float64 -> "toDouble"
        | Number Decimal -> "toDecimal"
        | _ -> failwith "Unexpected non-number type"
    let sourceType =
        match sourceType with
        | EnumType(NumberEnumType, _) -> Number Int32
        | t -> t
    match sourceType with
    | Char -> Helper.InstanceCall(args.Head, "charCodeAt", targetType, [makeIntConst 0])
    | String ->
        match targetType with
        | Builtin (BclInt64|BclUInt64 as kind) ->
            let unsigned = kind = BclUInt64
            let args = [args.Head]@[makeBoolConst unsigned]@args.Tail
            Helper.CoreCall("Long", "fromString", targetType, args)
        | _ -> Helper.CoreCall("Int32", "parse", targetType, args)
    | Builtin BclBigInt ->
        let meth = castBigIntMethod targetType
        Helper.CoreCall("BigInt", meth, targetType, args)
    | Number _ | Builtin (BclInt64 | BclUInt64) as typeFrom ->
        match targetType with
        | typeTo when needToCast typeFrom typeTo ->
            match typeFrom, typeTo with
            | Builtin (BclUInt64|BclInt64), Number _ ->
                Helper.CoreCall("Long", "toNumber", targetType, args)
            | Number (Decimal|Float), (Number Integer | Builtin(BclInt64|BclUInt64)) when round ->
                Helper.CoreCall("Util", "round", targetType, args)
            | _, _ -> args.Head
            |> List.singleton
            |> emitCast typeTo
        | Builtin (BclUInt64|BclInt64 as kind) ->
            emitLong (kind = BclUInt64) [args.Head]
        | Number _ -> args.Head
        | _ -> args.Head
    | _ -> args.Head

// TODO: Should we pass the empty list representation here?
let toList returnType expr =
    Helper.CoreCall("Seq", "toList", returnType, [expr])

let toArray (com: ICompiler) returnType expr =
    match expr, returnType with
    // Typed arrays
    | _, Array(Number numberKind) when com.Options.typedArrays ->
        Helper.GlobalCall(getTypedArrayName com numberKind, returnType, [expr], memb="from")
    | _ -> Helper.GlobalCall("Array", returnType, [expr], memb="from")

let toSeq returnType (expr: Expr) =
    match expr.Type with
    | List _ -> Helper.CoreCall("List", "toSeq", returnType, [expr])
    | _ -> expr // TODO

let getZero = function
    | Char | String -> makeStrConst ""
    | Builtin BclTimeSpan -> makeIntConst 0
    | Builtin BclDateTime as t -> Helper.CoreCall("Date", "minValue", t, [])
    | Builtin BclDateTimeOffset as t -> Helper.CoreCall("DateOffset", "minValue", t, [])
    | Builtin FSharpSet as t -> Helper.CoreCall("Set", "create", t, [])
    | Builtin (BclInt64|BclUInt64) as t -> Helper.CoreCall("Long", "fromInt", t, [makeIntConst 0])
    | Builtin BclBigInt as t -> Helper.CoreCall("BigInt", "fromInt32", t, [makeIntConst 0])
    // TODO: Calls to custom Zero implementation
    | _ -> makeIntConst 0

let applyOp (com: ICompiler) ctx r t opName (args: Expr list) argTypes genArgs =
    let (|CustomOp|_|) com opName argTypes =
        let tryFindMember com (ent: FSharpEntity) opName argTypes =
            FSharp2Fable.TypeHelpers.tryFindMember com ent opName false argTypes
        argTypes |> List.tryPick (function
            | DeclaredType(ent,_) -> tryFindMember com ent opName argTypes
            | _ -> None)
    let unOp operator operand =
        Operation(UnaryOperation(operator, operand), t, r)
    let binOp op left right =
        Operation(BinaryOperation(op, left, right), t, r)
    let logicOp op left right =
        Operation(LogicalOperation(op, left, right), Boolean, r)
    let nativeOp opName argTypes args =
        match opName, args with
        | Operators.addition, [left; right] -> binOp BinaryPlus left right
        | Operators.subtraction, [left; right] -> binOp BinaryMinus left right
        | Operators.multiply, [left; right] -> binOp BinaryMultiply left right
        | Operators.division, [left; right] ->
            match argTypes with
            // Floor result of integer divisions (see #172)
            | Number Integer::_ -> binOp BinaryDivide left right |> fastIntFloor
            | _ -> binOp BinaryDivide left right
        | Operators.modulus, [left; right] -> binOp BinaryModulus left right
        | Operators.leftShift, [left; right] -> binOp BinaryShiftLeft left right
        | Operators.rightShift, [left; right] ->
            match argTypes with
            | Number UInt32::_ -> binOp BinaryShiftRightZeroFill left right // See #646
            | _ -> binOp BinaryShiftRightSignPropagating left right
        | Operators.bitwiseAnd, [left; right] -> binOp BinaryAndBitwise left right
        | Operators.bitwiseOr, [left; right] -> binOp BinaryOrBitwise left right
        | Operators.exclusiveOr, [left; right] -> binOp BinaryXorBitwise left right
        | Operators.booleanAnd, [left; right] -> logicOp LogicalAnd left right
        | Operators.booleanOr, [left; right] -> logicOp LogicalOr left right
        | Operators.logicalNot, [operand] -> unOp UnaryNotBitwise operand
        | Operators.unaryNegation, [operand] -> unOp UnaryMinus operand
        | _ -> "Unknown operator: " + opName |> addErrorAndReturnNull com r
    let argTypes = resolveArgTypes argTypes genArgs
    match argTypes with
    | Builtin(BclInt64|BclUInt64|BclBigInt|BclDateTime|BclDateTimeOffset|FSharpSet as bt)::_ ->
        Helper.CoreCall(coreModFor bt, opName, t, args, argTypes, ?loc=r)
    | Builtin BclTimeSpan::_ ->
        nativeOp opName argTypes args
    | CustomOp com opName m ->
        let genArgs = genArgs |> Seq.map snd
        FSharp2Fable.Util.makeCallFrom com ctx r t genArgs None args m
    | _ -> nativeOp opName argTypes args

let equals r equal (args: Expr list) =
    let op equal =
        if equal then BinaryEqualStrict else BinaryUnequalStrict

    let is equal expr =
        if equal
        then expr
        else makeUnOp None Boolean expr UnaryNot

    match args with
    | [ExprType(Builtin(BclGuid|BclTimeSpan)) as left; right]
    | [ExprType(Boolean | Char | String | Number _ | EnumType _) as left; right] ->
        op equal |> makeBinOp r Boolean left right

    | ExprType(Builtin(BclDateTime|BclDateTimeOffset))::_ ->
        Helper.CoreCall("Date", "equals", Boolean, args, ?loc=r) |> is equal

    | ExprType(Builtin(FSharpSet|FSharpMap)) as callee::args ->
        Helper.InstanceCall(callee, "Equals", Boolean, args) |> is equal

    | ExprType(Builtin(BclInt64|BclUInt64|BclBigInt as bt))::_ ->
        Helper.CoreCall(coreModFor bt, "equals", Boolean, args, ?loc=r) |> is equal

    | ExprType(Array _ | List _ | Tuple _)::_ ->
        Helper.CoreCall("Util", "equalArrays", Boolean, args, ?loc=r) |> is equal
    | ExprType(DeclaredType(ent,_))::_ when ent.IsFSharpUnion ->
        Helper.CoreCall("Util", "equalArrays", Boolean, args, ?loc=r) |> is equal

    | ExprType(DeclaredType(ent,_))::_ when ent.IsFSharpRecord ->
        Helper.CoreCall("Util", "equalObjects", Boolean, args, ?loc=r) |> is equal

    | _ -> Helper.CoreCall("Util", "equals", Boolean, args, ?loc=r) |> is equal

/// Compare function that will call Util.compare or instance `CompareTo` as appropriate
/// If passed an optional binary operator, it will wrap the comparison like `comparison < 0`
let compare r (args: Expr list) op =
    let wrapWith op comparison =
        match op with
        | None -> comparison
        | Some op -> makeEqOp r comparison (makeIntConst 0) op

    match args with
    | [ExprType(Builtin(BclGuid|BclTimeSpan)) as left; right]
    | [ExprType(Boolean | Char | String | Number _ | EnumType _) as left; right] ->
        match op with
        | Some op -> makeEqOp r left right op
        | None -> Helper.CoreCall("Util", "comparePrimitives", Number Int32, args, ?loc=r)

    | ExprType(Builtin(BclDateTime|BclDateTimeOffset))::_ ->
        Helper.CoreCall("Date", "compare", Number Int32, args, ?loc=r) |> wrapWith op

    | ExprType(Builtin(BclInt64|BclUInt64|BclBigInt as bt))::_ ->
        Helper.CoreCall(coreModFor bt, "compare", Number Int32, args, ?loc=r) |> wrapWith op

    | ExprType(Array _ | List _ | Tuple _)::_ ->
        Helper.CoreCall("Util", "compareArrays", Boolean, args, ?loc=r) |> wrapWith op
    | ExprType(DeclaredType(ent,_))::_ when ent.IsFSharpUnion ->
        Helper.CoreCall("Util", "compareArrays", Boolean, args, ?loc=r) |> wrapWith op

    | ExprType(DeclaredType(ent,_))::_ when ent.IsFSharpRecord ->
        Helper.CoreCall("Util", "compareObjects", Boolean, args, ?loc=r) |> wrapWith op

    | _ -> Helper.CoreCall("Util", "compare", Number Int32, args, ?loc=r) |> wrapWith op

let makeComparerFunction typArg =
    let t = FunctionType(DelegateType [typArg; typArg], Number Int32)
    match typArg with
    | Builtin(BclGuid|BclTimeSpan)
    | Boolean | Char | String | Number _ | EnumType _ ->
        makeCoreRef t "Util" "comparePrimitives"
    | Builtin(BclDateTime|BclDateTimeOffset) ->
        makeCoreRef t "Date" "compare"
    | Builtin(BclInt64|BclUInt64|BclBigInt as bt) ->
       makeCoreRef t (coreModFor bt) "compare"
    | Array _ | List _ | Tuple _ ->
        makeCoreRef t "Util" "compareArrays"
    | DeclaredType(ent,_) when ent.IsFSharpUnion ->
        makeCoreRef t "Util" "compareArrays"
    | DeclaredType(ent,_) when ent.IsFSharpRecord ->
        makeCoreRef t "Util" "compareObjects"
    | _ ->
        makeCoreRef t "Util" "compare"

// TODO!!! This and the following method must be adjusted to new fable-core Map and Set modules
let makeMapOrSetCons com r t (i: CallInfo) modName args =
    let typArg = firstGenArg com r i.GenericArgs
    let comparer =
        let fn = makeComparerFunction typArg
        Helper.CoreCall("Util", "Comparer", Any, [fn], isConstructor=true)
    let args =
        match args with
        | [iterable] -> [iterable; comparer]
        | args -> args
    Helper.CoreCall(modName, "create", t, args, i.SignatureArgTypes, ?loc=r)

let makeDictionaryOrHashSet com r t (i: CallInfo) modName forceFSharp args =
    let makeFSharp typArg args =
        let args =
            match args with
            | [iterable] -> [iterable; Helper.CoreCall("Util", "Comparer", Any, [makeComparerFunction typArg])]
            | args -> args
        Helper.CoreCall(modName, "create", t, args, i.SignatureArgTypes, ?loc=r)
    let typArg = firstGenArg com r i.GenericArgs
    if forceFSharp
    then makeFSharp typArg args
    else
        match typArg with
        | Builtin(BclInt64|BclUInt64|BclBigInt)
        | Array _ | List _ | Tuple _ ->
            makeFSharp typArg args
        | Builtin(BclGuid|BclTimeSpan) ->
            Helper.GlobalCall(modName, t, args, i.SignatureArgTypes, ?loc=r)
        | DeclaredType _ ->
            makeFSharp typArg args
        | _ ->
            Helper.GlobalCall(modName, t, args, i.SignatureArgTypes, ?loc=r)

// TODO: Try to compile as literal object
let makeJsLiteralFromLambda arg =
    // match arg with
    // | Value(Lambda(args, lambdaBody, _)) ->
    //     (Some [], flattenSequential lambdaBody)
    //     ||> List.fold (fun acc statement ->
    //         match acc, statement with
    //         // Set of callee: Expr * property: Expr option * value: Expr * range: SourceLocation option
    //         | Some acc, Set(_,Some(Value(StringConst prop)),value,_) ->
    //             (prop, value)::acc |> Some
    //         | _ -> None)
    // | _ -> None
    // |> Option.map (makeJsObject r)
    // |> Option.defaultWith (fun () ->
        Helper.CoreCall("Util", "jsOptions", Any, [arg])
    // )

// TODO: Try to compile as literal object
let makeJsLiteral caseRule keyValueList =
    // let rec (|Fields|_|) caseRule = function
    //     | Value(ArrayConst(ArrayValues exprs, _)) ->
    //         (Some [], exprs) ||> List.fold (fun acc e ->
    //             acc |> Option.bind (fun acc ->
    //                 match e with
    //                 | Value(TupleConst [Value(StringConst key); value]) ->
    //                     (key, value)::acc |> Some
    //                 | UnionCons(tag, fields, cases) ->
    //                     let key =
    //                         let key = cases |> List.item tag |> fst
    //                         match caseRule with
    //                         | CaseRules.LowerFirst -> Naming.lowerFirst key
    //                         | _ -> key
    //                     let value =
    //                         match fields with
    //                         | [] -> Value(BoolConst true) |> Some
    //                         | [CoreCons "List" "default" []] -> makeJsObject r [] |> Some
    //                         | [CoreMeth "List" "ofArray" [Fields caseRule fields]] -> makeJsObject r fields |> Some
    //                         | [expr] ->
    //                             match expr.Type with
    //                             // Lists references must be converted to objects at runtime
    //                             | DeclaredType(fullName,_) when fullName = "Microsoft.FSharp.Collections.FSharpList" -> None
    //                             | _ -> Some expr
    //                         | exprs -> Value(ArrayConst(ArrayValues exprs, Any)) |> Some
    //                     value |> Option.map (fun value -> (key, value)::acc)
    //                 | _ -> None))
    //         |> Option.map List.rev
    //     | _ -> None
    // match keyValueList with
    // | CoreCons "List" "default" [] -> makeJsObject r []
    // | CoreMeth "List" "ofArray" [Fields caseRule fields] -> makeJsObject r fields
    // | _ ->
        Helper.CoreCall("Util", "createObj", Any, [keyValueList; caseRule |> int |> makeIntConst])

let fableCoreLib (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName, args with
    | "importDynamic", _ ->
        Helper.GlobalCall("import", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | Naming.StartsWith "import" suffix, _ ->
        let fail() =
            sprintf "Fable.Core.JsInterop.import%s only accepts literal strings" suffix
            |> addError com r
        let selector, args =
            match suffix with
            | "Member" -> Naming.placeholder, args
            | "Default" -> "default", args
            | "SideEffects" -> "", args
            | "All" -> "*", args
            | _ ->
                match args with
                | Value(StringConstant selector)::args -> selector, args
                | _ -> fail(); "*", [makeStrConst "unknown"]
        let path =
            match args with
            | [Value(StringConstant path)] -> path
            | _ -> fail(); "unknown"
        Import(selector, path, CustomImport, t) |> Some
    // Dynamic casting, erase
    | "op_BangBang", _ | "op_BangHat", _ -> List.tryHead args
    | "op_Dynamic", [left; memb] -> getExpr r t left memb |> Some
    | "op_DynamicAssignment", [callee; prop; value] ->
        Set(callee, ExprSet prop, value, r) |> Some
    | ("op_Dollar"|"createNew" as m), callee::args ->
        let argInfo = { argInfo None args None with Spread = TupleSpread }
        if m = "createNew"
        then constructorCall r t argInfo callee |> Some
        else staticCall r t argInfo callee |> Some
    | "op_EqualsEqualsGreater", _ ->
        NewTuple args |> Value |> Some
    | "createObj", [kvs] ->
        makeJsLiteral Fable.Core.CaseRules.None kvs |> Some
     | "keyValueList", _ ->
        match args with
        | [Value(Enum(NumberEnum rule, _)); keyValueList] ->
            let caseRule: Fable.Core.CaseRules = enum (int rule)
            makeJsLiteral caseRule keyValueList |> Some
        | [caseRule; keyValueList] ->
            Helper.CoreCall("Util", "createObj", t, [keyValueList; caseRule], ?loc=r) |> Some
        | _ -> None
    | "jsOptions", [arg] ->
        makeJsLiteralFromLambda arg |> Some
    | "jsThis", _ ->
        This t |> Value |> Some
    | "createEmpty", _ ->
        objExpr t [] |> Some
    | "nameof", _ ->
        match args with
        | [Nameof name] -> name
        | _ -> "Cannot infer name of expression" |> addError com r; "unknown"
        |> makeStrConst |> Some
    | "nameofLambda", _ ->
        match args with
        | [Function(_, Nameof name, _)] -> name
        | _ -> "Cannot infer name of expression" |> addError com r; "unknown"
        |> makeStrConst |> Some
    | "AreEqual", _ ->
        Helper.CoreCall("Util", "assertEqual", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | ("async.AwaitPromise.Static"|"async.StartAsPromise.Static" as m), _ ->
        let meth =
            if m = "async.AwaitPromise.Static"
            then "awaitPromise" else "startAsPromise"
        Helper.CoreCall("Async", meth, t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | "jsNative", _ ->
        // TODO: Fail at compile time?
        addWarning com r "jsNative is being compiled without replacement, this will fail at runtime."
        let runtimeMsg =
            "A function supposed to be replaced by JS native code has been called, please check."
            |> StringConstant |> Value
        Throw(error runtimeMsg, t, r) |> Some
    | _ -> None

let references (_: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName, thisArg, args with
    | ".ctor", _, [arg] ->
        objExpr t ["contents", arg] |> Some
    | "get_Value", Some callee, _ ->
        get r t callee "contents" |> Some
    | "set_Value", Some callee, [value] ->
        Set(callee, makeStrConst "contents" |> ExprSet, value, r) |> Some
    | _ -> None

let fsFormat (_: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName, thisArg, args with
    | "get_Value", Some callee, _ ->
        get None t callee "input" |> Some
    | "PrintFormatToStringThen", _, _ ->
        match args with
        | [_] -> Helper.CoreCall("String", "toText", t, args, i.SignatureArgTypes, ?loc=r) |> Some
        | [cont; fmt] -> Helper.InstanceCall(fmt, "cont", t, [cont]) |> Some
        | _ -> None
    | "PrintFormatToString", _, _ ->
        Helper.CoreCall("String", "toText", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | "PrintFormatLine", _, _ ->
        Helper.CoreCall("String", "toConsole", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | ("PrintFormatToTextWriter"|"PrintFormatLineToTextWriter"), _, _::args ->
        // addWarning com r "fprintfn will behave as printfn"
        Helper.CoreCall("String", "toConsole", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | "PrintFormat", _, _ ->
        // addWarning com r "Printf will behave as printfn"
        Helper.CoreCall("String", "toConsole", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | "PrintFormatThen", _, arg::callee::_ ->
        Helper.InstanceCall(callee, "cont", t, [arg]) |> Some
    | "PrintFormatToStringThenFail", _, _ ->
        Helper.CoreCall("String", "toFail", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | ".ctor", _, arg::_ ->
        Helper.CoreCall("String", "printf", t, [arg], i.SignatureArgTypes, ?loc=r) |> Some
    | _ -> None

let operators (com: ICompiler) (ctx: Context) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let curriedApply r t applied args =
        Operation(CurriedApply(applied, args), t, r)

    let compose (com: ICompiler) r t f1 f2 =
        let argType, retType =
            match t with
            | FunctionType(LambdaType argType, retType) -> argType, retType
            | _ -> Any, Any
        let tempVar = com.GetUniqueVar("arg") |> makeTypedIdent argType
        let body =
            [IdentExpr tempVar]
            |> curriedApply None Any f1
            |> List.singleton
            |> curriedApply r retType f2
        Function(Lambda tempVar, body, None)

    let math r t (args: Expr list) argTypes methName =
        Helper.GlobalCall("Math", t, args, argTypes, memb=Naming.lowerFirst methName, ?loc=r)

    match i.CompiledName, args with
    | "DefaultArg", [arg1; arg2] ->
        if hasDoubleEvalRisk arg1
        then Helper.CoreCall("Option", "defaultArg", t, args, i.SignatureArgTypes, ?loc=r) |> Some
        else
            let cond = Test(arg1, OptionTest false, None)
            IfThenElse(cond, arg1, arg2) |> Some
    | "DefaultAsyncBuilder", _ ->
        makeCoreRef t "AsyncBuilder" "singleton" |> Some
    // Erased operators. TODO: Use Cast?
    // KeyValuePair is already compiled as a tuple
    | ("KeyValuePattern"|"Identity"|"Box"|"Unbox"|"ToEnum"), _ -> List.tryHead args
    // Cast to unit to make sure nothing is returned when wrapped in a lambda, see #1360
    | "Ignore", [arg]  -> Cast(arg, Unit) |> Some
    // TODO: Number and String conversions
    | ("ToSByte"|"ToByte"|"ToInt8"|"ToUInt8"|"ToInt16"|"ToUInt16"|"ToInt"|"ToUInt"|"ToInt32"|"ToUInt32"|"ToInt64"|"ToUInt64"), _ ->
        let sourceType = firstGenArg com r i.GenericArgs
        toInt false sourceType t args |> Some
    | ("ToSingle"|"ToDouble"|"ToDecimal"), _ ->
        let sourceType = firstGenArg com r i.GenericArgs
        toFloat sourceType t args |> Some
    | "ToChar", _ -> toChar (firstGenArg com r i.GenericArgs) args |> Some
    | "ToString", _ -> toString (firstGenArg com r i.GenericArgs) args |> Some
    // The cast will be resolved in a different step
    | "CreateSequence", [xs] -> Cast(xs, t) |> Some
    | "CreateDictionary", _ ->
        makeDictionaryOrHashSet com r t i "Map" false args |> Some
    | "CreateSet", _ ->
        makeMapOrSetCons com r t i "Set" args |> Some
    // Ranges
    | ("op_Range"|"op_RangeStep"), _ ->
        let meth =
            match firstGenArg com r i.GenericArgs with
            | Char -> "rangeChar"
            | _ -> if i.CompiledName = "op_Range" then "range" else "rangeStep"
        Helper.CoreCall("Seq", meth, t, args, i.SignatureArgTypes, ?loc=r) |> Some
    // Pipes and composition
    | "op_PipeRight", [x; f]
    | "op_PipeLeft", [f; x] -> curriedApply r t f [x] |> Some
    | "op_PipeRight2", [x; y; f]
    | "op_PipeLeft2", [f; x; y] -> curriedApply r t f [x; y] |> Some
    | "op_PipeRight3", [x; y; z; f]
    | "op_PipeLeft3", [f; x; y; z] -> curriedApply r t f [x; y; z] |> Some
    | "op_ComposeRight", [f1; f2] -> compose com r t f1 f2 |> Some
    | "op_ComposeLeft", [f2; f1] -> compose com r t f1 f2 |> Some
    // Strings
    | ("PrintFormatToString"             // sprintf
    |  "PrintFormatToStringThen"         // Printf.sprintf
    |  "PrintFormat" | "PrintFormatLine" // printf / printfn
    |  "PrintFormatThen"                 // Printf.kprintf
    |  "PrintFormatToStringThenFail"), _ ->  // Printf.failwithf
        fsFormat com r t i thisArg args
    // Exceptions
    | "FailWith", [msg] | "InvalidOp", [msg] ->
        Throw(error msg, t, r) |> Some
    | "InvalidArg", [argName; msg] ->
        let msg = add (add msg (s "\\nParameter name: ")) argName
        Throw(error msg, t, r) |> Some
    | "Raise", [arg] -> Throw(arg, t, r) |> Some
    | "Reraise", _ ->
        match ctx.CaughtException with
        | Some ex -> Throw(IdentExpr ex, t, r) |> Some
        | None ->
            addError com r "`reraise` used in context where caught exception is not available, please report"
            Throw(error (s ""), t, r) |> Some
    // Math functions
    // TODO: optimize square pow: x * x
    | "Pow", _ | "PowInteger", _ | "op_Exponentiation", _ ->
        math r t args i.SignatureArgTypes "pow" |> Some
    | "Ceil", _ | "Ceiling", _ ->
        math r t args i.SignatureArgTypes "ceil" |> Some
    | "Log", [arg1; arg2] ->
        // "Math.log($0) / Math.log($1)"
        let dividend = math None t [arg1] (List.take 1 i.SignatureArgTypes) "log"
        let divisor = math None t [arg2] (List.skip 1 i.SignatureArgTypes) "log"
        makeBinOp r t dividend divisor BinaryDivide |> Some
    | "Abs", _ ->
        match resolveArgTypes i.SignatureArgTypes i.GenericArgs with
        | Builtin(BclInt64 | BclBigInt as bt)::_  ->
            Helper.CoreCall(coreModFor bt, "abs", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
        | _ -> math r t args i.SignatureArgTypes i.CompiledName |> Some
    | "Acos", _ | "Asin", _ | "Atan", _ | "Atan2", _
    | "Cos", _ | "Exp", _ | "Floor", _ | "Log", _ | "Log10", _
    | "Sin", _ | "Sqrt", _ | "Tan", _ ->
        math r t args i.SignatureArgTypes i.CompiledName |> Some
    | "Round", _ ->
        Helper.CoreCall("Util", "round", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | "Sign", _ ->
        let args =
            match args with
            | ((ExprType(Builtin(BclInt64|BclBigInt) as t)) as arg)::_ ->
                toFloat t (Number Float64) [arg] |> List.singleton
            | _ -> args
        Helper.CoreCall("Util", "sign", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    // Numbers
    | ("Infinity"|"InfinitySingle"), _ ->
        Helper.GlobalIdent("Number", "POSITIVE_INFINITY", t, ?loc=r) |> Some
    | ("NaN"|"NaNSingle"), _ ->
        Helper.GlobalIdent("Number", "NaN", t, ?loc=r) |> Some
    // TODO: Move these optimizations to special pass and check side effects
    // | "Fst", [Value(NewTuple(fst::_))] -> Some fst
    // | "Snd", [Value(NewTuple(_::snd::_))] -> Some snd
    | "Fst", [tup] -> Get(tup, TupleGet 0, t, r) |> Some
    | "Snd", [tup] -> Get(tup, TupleGet 1, t, r) |> Some
    // Reference
    | "op_Dereference", [arg] -> get r t arg "contents" |> Some
    | "op_ColonEquals", [o; v] -> Set(o, makeStrConst "contents" |> ExprSet, v, r) |> Some
    | "Ref", [arg] -> objExpr t ["contents", arg] |> Some
    | ("Increment"|"Decrement"), _ ->
        if i.CompiledName = "Increment" then "void($0.contents++)" else "void($0.contents--)"
        |> emitJs r t args |> Some
    // Concatenates two lists
    | "op_Append", _ -> Helper.CoreCall("List", "append", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | ("op_Inequality"|"Neq"), _ -> equals r false args |> Some
    | ("op_Equality"|"Eq"), _ -> equals r true args |> Some
    | "IsNull", [arg] -> makeEqOp r arg (Null arg.Type |> Value) BinaryEqual |> Some
    | "Hash", _ -> Helper.CoreCall("Util", "hash", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    // Comparison
    | "Compare", _ -> compare r args None |> Some
    | ("op_LessThan"|"Lt"), _ -> compare r args (Some BinaryLess) |> Some
    | ("op_LessThanOrEqual"|"Lte"), _ -> compare r args (Some BinaryLessOrEqual) |> Some
    | ("op_GreaterThan"|"Gt"), _ -> compare r args (Some BinaryGreater) |> Some
    | ("op_GreaterThanOrEqual"|"Gte"), _ -> compare r args (Some BinaryGreaterOrEqual) |> Some
    | ("Min"|"Max"), [arg1; arg2] ->
        let op = if i.CompiledName = "Min" then BinaryLess else BinaryGreater
        let comparison = compare r args (Some op)
        IfThenElse(comparison, arg1, arg2) |> Some
    | "Not", [operand] -> // TODO: Check custom operator?
        makeUnOp r t operand UnaryNot |> Some
    | SetContains Operators.standardSet, _ ->
        applyOp com ctx r t i.CompiledName args i.SignatureArgTypes i.GenericArgs |> Some
    // // Type ref
    // | "typeOf" | "typeDefOf" ->
    //     None // TODO
    //     // info.memberGenArgs.Head
    //     // |> resolveTypeRef com info (info.memberName = "typeOf")
    //     // |> Some
    | _ -> None

let chars (com: ICompiler) r t (i: CallInfo) (_: Expr option) (args: Expr list) =
    let icall r t args argTypes memb  =
        match args, argTypes with
        | thisArg::args, _::argTypes ->
            let info = argInfo (Some thisArg) args (Some argTypes)
            instanceCall r t info (makeStrConst memb |> Some) |> Some
        | _ -> None
    match i.CompiledName with
    | "ToUpper" -> icall r t args i.SignatureArgTypes "toLocaleUpperCase"
    | "ToUpperInvariant" -> icall r t args i.SignatureArgTypes "toUpperCase"
    | "ToLower" -> icall r t args i.SignatureArgTypes "toLocaleLowerCase"
    | "ToLowerInvariant" -> icall r t args i.SignatureArgTypes "toLowerCase"
    | "IsLetter" | "IsNumber" | "IsDigit"
    | "IsLetterOrDigit" | "IsWhiteSpace"
    | "IsUpper" | "IsLower"
    | "Parse" ->
        let methName =
            match i.CompiledName with
            | "IsNumber" ->
                addWarning com r "Char.IsNumber is compiled as Char.IsDigit"
                "isDigit"
            | methName -> Naming.lowerFirst methName
        let args, argTypes =
            match args with
            | [str; idx] -> [getExpr None Char str idx], [Char]
            | args -> args, i.SignatureArgTypes
        Helper.CoreCall("Char", methName, t, args, argTypes, ?loc=r) |> Some
    | _ -> None

let strings (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName, thisArg, args with
    | ".ctor", _, fstArg::_ ->
        match fstArg.Type with
        | String ->
            match args with
            | [_; _] -> emitJs r t args "Array($1 + 1).join($0)" |> Some // String(char, int)
            | _ -> addErrorAndReturnNull com r "Unexpected arguments in System.String constructor." |> Some
        | Array _ ->
            match args with
            | [_] -> emitJs r t args "$0.join('')" |> Some // String(char[])
            | [_; _; _] -> emitJs r t args "$0.join('').substr($1, $2)" |> Some // String(char[], int, int)
            | _ -> addErrorAndReturnNull com r "Unexpected arguments in System.String constructor." |> Some
        | _ ->
            fsFormat com r t i thisArg args
    | "get_Length", Some c, _ -> get r t c "length" |> Some
    | "Length", None, [arg] -> get r t arg "length" |> Some
    | "Equals", Some x, [y]
    | "Equals", None, [x; y] ->
        makeEqOp r x y BinaryEqualStrict |> Some
    | "Equals", Some x, [y; kind]
    | "Equals", None, [x; y; kind] ->
        let left = Helper.CoreCall("String", "compare", Number Int32, [x; y; kind])
        makeEqOp r left (makeIntConst 0) BinaryEqualStrict |> Some
    | "Contains", Some c, arg::_ ->
        if (List.length args) > 1 then
            addWarning com r "String.Contains: second argument is ignored"
        let left = Helper.InstanceCall(c, "indexOf", Number Int32, [arg])
        makeEqOp r left (makeIntConst 0) BinaryGreaterOrEqual |> Some
    | "StartsWith", Some c, [_str] ->
        let left = Helper.InstanceCall(c, "indexOf", Number Int32, args)
        makeEqOp r left (makeIntConst 0) BinaryEqualStrict |> Some
    | "StartsWith", c, [_str; _comp] ->
        Helper.CoreCall("String", "startsWith", t, args, i.SignatureArgTypes, ?thisArg=c, ?loc=r) |> Some
    | ReplaceName [ "Substring",        "substr"
                    "ToUpper",          "toLocaleUpperCase"
                    "ToUpperInvariant", "toUpperCase"
                    "ToLower",          "toLocaleLowerCase"
                    "ToLowerInvariant", "toLowerCase" ] methName, Some c, args ->
        Helper.InstanceCall(c, methName, t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | "Chars", _, _ ->
        Helper.CoreCall("String", "getCharAtIndex", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | ("IndexOf" | "LastIndexOf"), Some c, _ ->
        match args with
        | [ExprType Char]
        | [ExprType String]
        | [ExprType Char; ExprType(Number Int32)]
        | [ExprType String; ExprType(Number Int32)] ->
            Helper.InstanceCall(c, Naming.lowerFirst i.CompiledName, t, args, i.SignatureArgTypes, ?loc=r) |> Some
        | _ -> "The only extra argument accepted for String.IndexOf/LastIndexOf is startIndex."
               |> addErrorAndReturnNull com r |> Some
    | ("Trim" | "TrimStart" | "TrimEnd"), Some c, _ ->
        let side =
            match i.CompiledName with
            | "TrimStart" -> "start"
            | "TrimEnd" -> "end"
            | _ -> "both"
        Helper.CoreCall("String", "trim", t, (c::(makeStrConst side)::args), ?loc=r) |> Some
    | "ToCharArray", Some c, _ ->
        Helper.InstanceCall(c, "split", t, [makeStrConst ""]) |> Some
    | ("Iterate" | "IterateIndexed" | "ForAll" | "Exists"), _, _ ->
        // TODO!!! Cast the string to seq<char>
        Helper.CoreCall("Seq", Naming.lowerFirst i.CompiledName, t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | ("Map" | "MapIndexed" | "Collect"), _, _ ->
        // TODO!!! Cast the string to seq<char>
        let name = Naming.lowerFirst i.CompiledName
        emitJs r t [Helper.CoreCall("Seq", name, Any, args)] "Array.from($0).join('')" |> Some
    | "Concat", _, _ ->
        let args =
            if i.DeclaringEntityFullName = "System.String"
            then (makeStrConst "")::args else args
        Helper.CoreCall("String", "join", t, args, ?loc=r) |> Some
    | "Split", Some c, _ ->
        match args with
        // Optimization
        | [] -> Helper.InstanceCall(c, "split", t, [makeStrConst ""]) |> Some
        | [Value(StringConstant _) as separator]
        | [Value(NewArray(ArrayValues [separator],_))] ->
            Helper.InstanceCall(c, "split", t, [separator]) |> Some
        | [arg1; ExprType(EnumType _) as arg2] ->
            let arg1 =
                match arg1.Type with
                | Array _ -> arg1
                | _ -> Value(NewArray(ArrayValues [arg1], String))
            let args = [arg1; Value(Null Any); arg2]
            Helper.CoreCall("String", "split", t, c::args, ?loc=r) |> Some
        | args ->
            Helper.CoreCall("String", "split", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | "Filter", _, _ -> Helper.CoreCall("String", "filter", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | _ -> None

let arrayCons (com: ICompiler) genArg =
    match genArg with
    | Number numberKind when com.Options.typedArrays ->
        getTypedArrayName com numberKind |> makeIdentExpr
    | _ -> makeIdentExpr "Array"

let seqs (com: ICompiler) r (t: Type) (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName, args with
    | "ToArray", [arg] -> toArray com t arg |> Some
    // A export function 'length' method causes problems in JavaScript -- https://github.com/Microsoft/TypeScript/issues/442
    | "Length", _ ->
        Helper.CoreCall("Seq", "count", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | "ChunkBySize" | "Permute" as meth, [arg1; arg2] ->
        let arg2 = toArray com (Array Any) arg2
        let args =
            match meth, t with
            | "Permute", DeclaredType(_seq, [genArg]) ->
                [arg1; arg2] @ [arrayCons com genArg]
            | _ -> [arg1; arg2]
        let result = Helper.CoreCall("Array", Naming.lowerFirst meth, Any, args)
        Helper.CoreCall("Seq", "ofArray", t, [result]) |> Some
    | meth, _ -> Helper.CoreCall("Seq", Naming.lowerFirst meth, t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    // | _ -> None

let passArrayCons =
    HashSet [|"choose"; "collect"; "concat"; "distinct"; "distinctBy"; "groupBy"; "initialize"; "map"; "map2"; "map3"
              "mapFold"; "mapFoldBack"; "mapIndexed"; "mapIndexed2";"mapIndexed3"; "ofList"; "partition"; "permute"; "replicate"
              "reverse"; "scan"; "scanBack"; "singleton"; "skip"; "skipWhile"; "sortWith"; "take"; "takeWhile"; "unfold"|]

let nativeArrayFunctions =
    dict [| "Exists", "some"
            "Filter", "filter"
            "Find", "find"
            "FindIndex", "findIndex"
            "ForAll", "every"
            "Iterate", "forEach"
            "Reduce", "reduce"
            "ReduceBack", "reduceRight"
            "SortInPlace", "sort"
            "SortInPlaceWith", "sort" |]

let arrays (_: ICompiler) r (t: Type) (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match thisArg, args, i.CompiledName with
    | Some ar, _, "get_Length" -> get r t ar "length" |> Some
    | Some ar, [idx], "get_Item" -> getExpr r t ar idx |> Some
    | Some ar, [idx; value], "set_Item" -> Set(ar, ExprSet idx, value, r) |> Some
    | _ -> None

let listToArray com r t (li: Expr) =
    match li with
    | ListLiteral(exprs, t) -> NewArray(ArrayValues exprs, t) |> Value
    | _ ->
        let args = match t with Array genArg -> [li; arrayCons com genArg] | _ -> [li]
        Helper.CoreCall("Array", "ofList", t, args, ?loc=r)

let arrayModule (com: ICompiler) r (t: Type) (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let inline newArray size t =
        Value(NewArray(ArrayAlloc size, t))
    let createArray size value =
        match t with
        | Array(Number _ as t2) ->
            match value with
            | None -> newArray size t2
            | Some v -> Helper.InstanceCall(newArray size t2, "fill", t, [v])
        | Array Boolean ->
            let value = defaultArg value (makeBoolConst false)
            Helper.InstanceCall(newArray size Boolean, "fill", t, [value])
        // If we don't fill the array with null values some operations
        // may behave unexpectedly, like Array.prototype.reduce
        | t ->
            let t2 = match t with Array t2 -> t2 | _ -> Any
            let value = defaultArg value (Value(Null Any))
            Helper.InstanceCall(newArray size t2, "fill", t, [value])
    match args, i.CompiledName with
    | [arg], "ToSeq" -> Some arg
    | [arg], "OfSeq" -> toArray com t arg |> Some
    | [arg], "OfList" -> listToArray com r t arg |> Some
    | [arg], ("Length"|"Count") -> get r t arg "length" |> Some
    | [idx; ar], "Item" -> getExpr r t ar idx |> Some
    | [ar; idx], "Get" -> getExpr r t ar idx |> Some
    | [ar; idx; value], "Set" -> Set(ar, ExprSet idx, value, r) |> Some
    | [count; ar], ("Take"|"Truncate") -> Helper.InstanceCall(ar, "slice", t, [makeIntConst 0; count], ?loc=r) |> Some
    | [count; ar], "Skip" -> Helper.InstanceCall(ar, "slice", t, [count], ?loc=r) |> Some
    | [ar],        "Copy" -> Helper.InstanceCall(ar, "slice", t, args, ?loc=r) |> Some
    | [count], "ZeroCreate"    -> createArray count None |> Some
    | [count; value], "Create" -> createArray count (Some value) |> Some
    | _, "Empty" ->
        let t = match t with Array t -> t | _ -> Any
        newArray (makeIntConst 0) t |> Some
    | [ar], "IsEmpty" ->
        eq (get r (Number Int32) ar "length") (makeIntConst 0) |> Some
    | [ar], "Head" -> getExpr r t ar (makeIntConst 0) |> Some
    | [ar], "Tail" -> Helper.InstanceCall(ar, "slice", t, [makeIntConst 1], ?loc=r) |> Some

    // TODO!!!
//         | "sort" | "sortDescending" | "sortBy" | "sortByDescending" ->
//         | "sum" | "sumBy" ->
//         | "min" | "minBy" | "max" | "maxBy" ->

    | _, Patterns.DicContains nativeArrayFunctions meth ->
        let args, thisArg = List.splitLast args
        let argTypes = List.take (List.length args) i.SignatureArgTypes
        Helper.InstanceCall(thisArg, meth, t, args, argTypes, ?loc=r) |> Some
    | _, meth ->
        let meth = Naming.lowerFirst meth
        let args, meth =
            if meth = "sort"
            then (firstGenArg com r i.GenericArgs |> makeComparerFunction)::args, "sortWith"
            else args, meth
        let args =
            if passArrayCons.Contains(meth) then
                match meth, t with
                | ("mapFold"|"mapFoldBack"), Tuple[Array genArg;_]
                | "groupBy", Array(Tuple[_; Array genArg])
                | "partition", Tuple[Array genArg;_]
                | _, Array genArg -> args @ [arrayCons com genArg]
                | _ -> args @ [arrayCons com Any]
            else args
        Helper.CoreCall("Array", Naming.lowerFirst meth, t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some

let lists (com: ICompiler) r (t: Type) (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match thisArg, args, i.CompiledName with
    | None, [x], "Head"
    | Some x, _, "get_Head" -> Get(x, ListHead, t, r) |> Some
    | None, [x], "Tail"
    | Some x, _, "get_Tail" -> Get(x, ListTail, t, r) |> Some
    | _, _, "get_Length" -> Helper.CoreCall("List", "length", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | Some x, _, "get_Item" -> Helper.CoreCall("List", "item", t, (args@[x]), i.SignatureArgTypes, ?loc=r) |> Some
    | Some x, _, "get_IsEmpty" -> Test(x, ListTest false, r) |> Some
    | None, _, ("get_Empty" | "Empty") ->
        NewList(None, firstGenArg com r i.GenericArgs) |> Value |> Some
    | None, [h;t], "Cons" -> NewList(Some(h,t), firstGenArg com r i.GenericArgs) |> Value |> Some
    | None, [x], "ToSeq" -> Cast(x, t) |> Some
    | None, _, "OfSeq" -> Helper.CoreCall("Seq", "toList", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | None, [x], "ToArray" -> listToArray com r t x |> Some
    | None, _, meth ->
        let meth = Naming.lowerFirst meth
        Helper.CoreCall("List", meth, t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | _ -> None

let sets (com: ICompiler) r (t: Type) (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match thisArg, args, i.CompiledName with
    // | None, [x], "Head"
    // | Some x, _, "get_Head" -> Get(x, ListHead, t, r) |> Some
    // | None, [x], "Tail"
    // | Some x, _, "get_Tail" -> Get(x, ListTail, t, r) |> Some
    // | _, _, "get_Length" -> Helper.CoreCall("List", "length", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    // | Some x, _, "get_Item" -> Helper.CoreCall("List", "item", t, (args@[x]), i.SignatureArgTypes, ?loc=r) |> Some
    | Some x, _, "get_IsEmpty" -> Test(x, ListTest false, r) |> Some
    | None, _, ("get_IsEmpty" | "Empty") ->
        makeMapOrSetCons com r t i "Set" args |> Some
    // | None, [h;t], "Cons" -> NewList(Some(h,t), firstGenArg com r i.GenericArgs) |> Value |> Some
    // | None, [x], "ToSeq" -> Cast(x, t) |> Some
    // | None, _, "OfSeq" -> Helper.CoreCall("Seq", "toList", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    // | None, [x], "ToArray" -> listToArray com r t x |> Some
    | None, _, meth ->
        let meth = Naming.lowerFirst meth
        Helper.CoreCall("Set", meth, t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | _ -> None

// See fable-core/Option.ts for more info on how
// options behave in Fable runtime
let options (com: ICompiler) r (t: Type) (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let toArray r t arg =
        let ident = makeIdent (com.GetUniqueVar())
        let f =
            [IdentExpr ident]
            |> makeArray Any
            |> makeDelegate [ident]
        // Prevent functions being run twice, see #198
        Helper.CoreCall("Option", "defaultArg", t, [arg; makeArray Any []; f], ?loc=r)
    match i.CompiledName, thisArg, args with
    | "None", None, _ ->
        NewOption(None, t) |> Value |> Some
    | "Value", None, [c]
    | "get_Value", Some c, _ ->
        Get(c, OptionValue, t, r) |> Some
    // | "toObj" | "toNullable" | "flatten" ->
    //     ccall i "Option" "getValue" [getCallee i; makeBoolConst true] |> Some
    // | "ofObj" | "ofNullable" ->
    //     wrap i.returnType (getCallee i) |> Some
    // | "isSome" | "isNone" ->
    //     let op =
    //         if i.memberName = "isSome"
    //         then BinaryUnequal
    //         else BinaryEqual
    //     let comp =
    //         ([getCallee i; Fable.Value Fable.Null], op)
    //         ||> makeEqOp i.range
    //     match i.returnType with
    //     | Fable.Boolean _ -> Some comp
    //     // Hack to fix instance member calls (e.g., myOpt.IsSome)
    //     // For some reason, F# compiler expects them to be applicable
    //     | _ -> makeLambda [] comp |> Some
    | ("Map" | "Bind"), None, [f; arg] ->
        let fType, argType =
            match i.SignatureArgTypes with
            | [fType; argType] -> fType, argType
            | _ -> f.Type, arg.Type // unexpected
        let args = [arg; Value(NewOption(None, argType)); f]
        Helper.CoreCall("Option", "defaultArg", t, args, [argType; Option argType; fType],  ?loc=r) |> Some
    // | "filter" ->
    //     ccall i "Option" "filter" i.args |> Some
    // | "toArray" ->
    //     toArray i.range i.returnType i.args.Head |> Some
    // | "foldBack" ->
    //     let opt = toArray None Fable.Any i.args.Tail.Head
    //     let args = i.args.Head::opt::i.args.Tail.Tail
    //     ccall i "Seq" "foldBack" args |> Some
    // | "defaultValue" | "orElse" ->
    //     List.rev i.args
    //     |> ccall i "Option" "defaultArg" |> Some
    // | "defaultWith" | "orElseWith" ->
    //     List.rev i.args
    //     |> ccall i "Option" "defaultArgWith" |> Some
    // | "count" | "contains" | "exists"
    // | "fold" | "forAll" | "iterate" | "toList" ->
    //     let args =
    //         let args = List.rev i.args
    //         let opt = toArray None Fable.Any args.Head
    //         List.rev (opt::args.Tail)
    //     ccall i "Seq" i.memberName args |> Some
    // | "map2" | "map3" -> failwith "TODO"
    | _ -> None

let decimals (com: ICompiler) r (_: Type) (i: CallInfo) (_callee: Expr option) (args: Expr list) =
    match i.CompiledName, args with
    | ".ctor", [Value(NumberConstant(x, _))] ->
#if FABLE_COMPILER
        makeNumConst (float x) |> Some
#else
        makeDecConst (decimal(x)) |> Some
#endif
    | ".ctor", [Value(NewArray(ArrayValues arVals, _))] ->
        match arVals with
        | [ Value(NumberConstant(low, Int32))
            Value(NumberConstant(mid, Int32))
            Value(NumberConstant(high, Int32))
            Value(NumberConstant(scale, Int32)) ] ->
#if FABLE_COMPILER
                let x = (float ((uint64 (uint32 mid)) <<< 32 ||| (uint64 (uint32 low))))
                        / System.Math.Pow(10.0, float ((int scale) >>> 16 &&& 0xFF))
                makeNumConst (if scale < 0.0 then -x else x) |> Some
#else
                makeDecConst (new decimal([| int low; int mid; int high; int scale |])) |> Some
#endif
        | _ -> None
    | (".ctor" | "MakeDecimal"),
          [ Value(NumberConstant(low, Int32))
            Value(NumberConstant(mid, Int32))
            Value(NumberConstant(high, Int32))
            Value(BoolConstant isNegative)
            Value(NumberConstant(scale, UInt8)) ] ->
#if FABLE_COMPILER
                let x = (float ((uint64 (uint32 mid)) <<< 32 ||| (uint64 (uint32 low))))
                        / System.Math.Pow(10.0, float scale)
                makeNumConst (if isNegative then -x else x) |> Some
#else
                makeDecConst (new decimal(int low, int mid, int high, isNegative, byte scale)) |> Some
#endif
    | ".ctor", [IdentExpr _ as arg] ->
        addWarning com r "Decimals are implemented with floats."
        Some arg
    | ("Parse" | "TryParse"), _ ->
        None // TODO: parse com i true
    | _,_ -> None

// let bigint (com: ICompiler) (ctx: Context) r (t: Type) (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
//     let coreMod = coreModFor BclBigInt
//     match thisArg, i.CompiledName with
//     | None, ".ctor" ->
//         match i.SignatureArgTypes with
//         | [Builtin(BclInt64|BclUInt64)] -> coreCall r t i coreMod "fromInt64" args
//         | [_] -> coreCall r t i coreMod "fromInt32" args
//         | _ -> coreCall r t i coreMod "default" args
//         |> Some
//     | None, ("Zero"|"One"|"Two" as memb) ->
//         makeCoreRef t coreMod (Naming.lowerFirst memb) |> Some
//     | None, ("FromZero"|"FromOne" as memb) ->
//         let memb = memb.Replace("From", "") |> Naming.lowerFirst
//         makeCoreRef t coreMod memb |> Some
//     | None, "FromString" ->
//         coreCall r t i coreMod "parse" args |> Some
//     | None, meth -> coreCall r t i coreMod meth args |> Some
//     | Some _callee, _ ->
//         // icall i meth |> Some
//         "TODO: BigInt instance methods" |> addErrorAndReturnNull com r |> Some

// Compile static strings to their constant values
// reference: https://msdn.microsoft.com/en-us/visualfsharpdocs/conceptual/languageprimitives.errorstrings-module-%5bfsharp%5d
let errorStrings = function
    | "InputArrayEmptyString" -> s "The input array was empty" |> Some
    | "InputSequenceEmptyString" -> s "The input sequence was empty" |> Some
    | "InputMustBeNonNegativeString" -> s "The input must be non-negative" |> Some
    | _ -> None

let languagePrimitives (_com: ICompiler) _r _t (i: CallInfo) (_thisArg: Expr option) (_args: Expr list) =
    match i.CompiledName with
    // TODO: Check for types with custom zero/one (strings?)
    | "GenericZero" -> NumberConstant(0., Int32) |> Value |> Some
    | "GenericOne" -> NumberConstant(1., Int32) |> Value |> Some
    | _ -> None

let intrinsicFunctions (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName, thisArg, args with
    // Erased operators
    | "CheckThis", _, [arg]
    | "UnboxFast", _, [arg]
    | "UnboxGeneric", _, [arg] -> Some arg
    | "MakeDecimal", _, _ -> decimals com r t i thisArg args
    | "GetString", _, [ar; idx]
    | "GetArray", _, [ar; idx] -> getExpr r t ar idx |> Some
    | "SetArray", _, [ar; idx; value] -> Set(ar, ExprSet idx, value, r) |> Some
    | ("GetArraySlice" | "GetStringSlice"), None, [ar; lower; upper] ->
        let upper =
            match upper with
            | Value(Null _) -> getExpr None (Number Int32) ar (makeStrConst "length")
            | _ -> add upper (makeIntConst 1)
        Helper.InstanceCall(ar, "slice", t, [lower; upper], ?loc=r) |> Some
    | "SetArraySlice", None, args ->
        Helper.CoreCall("Array", "setSlice", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | "TypeTestGeneric", None, [expr] ->
        makeTypeTest com r expr (firstGenArg com r i.GenericArgs) |> Some
    | "CreateInstance", None, _ ->
        None // TODO
        // let typRef, args = resolveTypeRef com i false i.memberGenArgs.Head, []
        // Apply (typRef, args, ApplyCons, t, r) |> Some
    // reference: https://msdn.microsoft.com/visualfsharpdocs/conceptual/operatorintrinsics.powdouble-function-%5bfsharp%5d
    // Type: PowDouble : float -> int -> float
    // Usage: PowDouble x n
    | "PowDouble", None, _ ->
        Helper.GlobalCall("Math", t, args, i.SignatureArgTypes, memb="pow", ?loc=r) |> Some
    // reference: https://msdn.microsoft.com/visualfsharpdocs/conceptual/operatorintrinsics.rangechar-function-%5bfsharp%5d
    // Type: RangeChar : char -> char -> seq<char>
    // Usage: RangeChar start stop
    | "RangeChar", None, _ ->
        Helper.CoreCall("Seq", "rangeChar", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    // reference: https://msdn.microsoft.com/visualfsharpdocs/conceptual/operatorintrinsics.rangedouble-function-%5bfsharp%5d
    // Type: RangeDouble: float -> float -> float -> seq<float>
    // Usage: RangeDouble start step stop
    | "RangeDouble", None, _ ->
        Helper.CoreCall("Seq", "rangeStep", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | "RangeInt32", None, args ->
        Helper.CoreCall("Seq", "rangeStep", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | _ -> None

let funcs (_: ICompiler) r t (i: CallInfo) thisArg args =
    match i.CompiledName, thisArg with
    | "Adapt", _ -> List.tryHead args // TODO: What's this used for?
    | "Invoke", Some callee ->
        Helper.Application(callee, t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | _ -> None

let keyValuePairs (_: ICompiler) r t (i: CallInfo) thisArg args =
    match i.CompiledName, thisArg with
    | ".ctor", _ -> Value(NewTuple args) |> Some
    | "get_Key", Some c -> Get(c, TupleGet 0, t, r) |> Some
    | "get_Value", Some c -> Get(c, TupleGet 1, t, r) |> Some
    | _ -> None

let dictionaries (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let (|IDictionary|IEqualityComparer|Other|) = function
        | DeclaredType(ent,_) ->
            match ent.TryFullName with
            | Some "System.Collections.Generic.IDictionary`2" -> IDictionary
            | Some "System.Collections.Generic.IEqualityComparer`1" -> IEqualityComparer
            | _ -> Other
        | _ -> Other
    let inline makeComparer e =
        Helper.CoreCall("Util", "comparerFromEqualityComparer", Any, [e])
    let inline makeDic forceFSharpMap args =
        makeDictionaryOrHashSet com r t i "Map" forceFSharpMap args
    match i.CompiledName, thisArg with
    | ".ctor", _ ->
        match i.SignatureArgTypes with
        | [] | [IDictionary] ->
            makeDic false args |> Some
        | [IDictionary; IEqualityComparer] ->
            makeDic true [args.Head; makeComparer args.Tail.Head] |> Some
        | [IEqualityComparer] ->
            makeDic true [Value (Null Any); makeComparer args.Head] |> Some
        | [Number _] ->
            makeDic false [] |> Some
        | [Number _; IEqualityComparer] ->
            makeDic true [Value (Null Any); makeComparer args.Tail.Head] |> Some
        | _ -> None
    | "get_IsReadOnly", _ ->
        // TODO: Check for immutable maps with IDictionary interface
        makeBoolConst false |> Some
    | "get_Count", _ ->
        get r t thisArg.Value "size" |> Some
    // TODO: Check if the key allows for a JS Map (also "TryGetValue" below)
    | "ContainsValue", _ ->
        match thisArg, args with
        | Some c, [arg] -> Helper.CoreCall("Util", "containsValue", t, [arg; c], ?loc=r) |> Some
        | _ -> None
    | "TryGetValue", _ ->
        Helper.CoreCall("Util", "tryGetValue", t, args, i.SignatureArgTypes, ?thisArg=thisArg, ?loc=r) |> Some
    | ReplaceName ["get_Item",     "get"
                   "set_Item",     "set"
                   "get_Keys",     "keys"
                   "get_Values",   "values"
                   "ContainsKey",  "has"
                   "Clear",        "clear"
                   "Add",          "set"
                   "Remove",       "delete" ] methName, Some c ->
        Helper.InstanceCall(c, methName, t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | _ -> None

let hashSets (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let (|IEnumerable|IEqualityComparer|Other|) = function
        | DeclaredType(ent,_) ->
            match ent.TryFullName with
            | Some "System.Collections.Generic.IEnumerable`1" -> IEnumerable
            | Some "System.Collections.Generic.IEqualityComparer`1" -> IEqualityComparer
            | _ -> Other
        | _ -> Other
    let inline makeComparer e =
        Helper.CoreCall("Util", "comparerFromEqualityComparer", Any, [e])
    let inline makeHashSet forceFSharpMap args =
        makeDictionaryOrHashSet com r t i "Set" forceFSharpMap args
    match i.CompiledName, thisArg, args with
    | ".ctor", _, _ ->
        match i.SignatureArgTypes with
        | [] | [IEnumerable] ->
            makeHashSet false args |> Some
        | [IEnumerable; IEqualityComparer] ->
            [args.Head; makeComparer args.Tail.Head]
            |> makeHashSet true |> Some
        | [IEqualityComparer] ->
            [Value (Null Any); makeComparer args.Head]
            |> makeHashSet true |> Some
        | _ -> None
    | "get_Count", _, _ -> get r t thisArg.Value "size" |> Some
    | "get_IsReadOnly", _, _ -> BoolConstant false |> Value |> Some
    // TODO!!!: Check key type
    | ReplaceName ["Clear",    "clear"
                   "Contains", "has"
                   "Remove",   "delete" ] methName, Some c, args ->
        Helper.InstanceCall(c, methName, t, args, i.SignatureArgTypes, ?loc=r) |> Some
    // TODO: Check if the value allows for a JS Set (also "TryGetValue" below)
    | "Add", Some c, [arg] ->
        Helper.CoreCall("Util", "addToSet", t, [arg; c], ?loc=r) |> Some
    | ("isProperSubsetOf" | "isProperSupersetOf"
    |  "unionWith" | "intersectWith" | "exceptWith"
    |  "isSubsetOf" | "isSupersetOf" | "copyTo" as methName), Some c, args ->
        let methName =
            let m = match methName with "exceptWith" -> "differenceWith" | m -> m
            m.Replace("With", "InPlace")
        Helper.CoreCall("Set", methName, t, c::args, ?loc=r) |> Some
    // TODO
    // | "setEquals"
    // | "overlaps"
    // | "symmetricExceptWith"
    | _ -> None

let exceptions (_: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName, thisArg with
    | ".ctor", _ -> Helper.ConstructorCall(makeIdentExpr "Error", t, args, ?loc=r) |> Some
    | "get_Message", Some e -> get r t e "message" |> Some
    | "get_StackTrace", Some e -> get r t e "stack" |> Some
    | _ -> None

let objects (_: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName, thisArg, args with
    | ".ctor", _, _ -> objExpr t [] |> Some
    | "GetHashCode", Some arg, _ ->
        Helper.CoreCall("Util", "getHashCode", t, [arg]) |> Some
    | "ToString", Some arg, _ ->
        Helper.CoreCall("Util", "toString", t, [arg]) |> Some
    | "ReferenceEquals", _, [left; right] ->
        makeBinOp r Boolean left right BinaryEqualStrict |> Some
    | "Equals", Some arg1, [arg2]
    | "Equals", None, [arg1; arg2] ->
        Helper.CoreCall("Util", "equals", t, [arg1; arg2], ?loc=r) |> Some
    | _ -> None

let unchecked (com: ICompiler) r t (i: CallInfo) (_: Expr option) (args: Expr list) =
    match i.CompiledName with
    | "DefaultOf" -> firstGenArg com r i.GenericArgs |> defaultof |> Some
    | "Hash" -> Helper.CoreCall("Util", "hash", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | "Equals" -> Helper.CoreCall("Util", "equals", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | "Compare" -> Helper.CoreCall("Util", "compare", t, args, i.SignatureArgTypes, ?loc=r) |> Some
    | _ -> None

let enums (_: ICompiler) r _ (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match thisArg, i.CompiledName, args with
    | Some this, "HasFlag", [arg] ->
        // x.HasFlags(y) => (int x) &&& (int y) <> 0
        makeBinOp r (Number Int32) this arg BinaryAndBitwise
        |> fun bitwise -> makeEqOp r bitwise (makeIntConst 0)BinaryUnequal
        |> Some
    | _ -> None

let log (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let v =
        match args with
        | [] -> []
        | [v] -> [v]
        | (Value (StringConstant _))::_ ->
            Helper.CoreCall("String", "format", t, args, i.SignatureArgTypes)
            |> List.singleton
        | _ -> [args.Head]
    Helper.GlobalCall("console", t, args, memb="log", ?loc=r)

let bitConvert (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let memberName =
        if i.CompiledName = "GetBytes" then
            match args.Head.Type with
            | Boolean -> "getBytesBoolean"
            | Char | String -> "getBytesChar"
            | Number Int16 -> "getBytesInt16"
            | Number Int32 -> "getBytesInt32"
            | Builtin BclInt64 -> "getBytesInt64"
            | Number UInt16 -> "getBytesUInt16"
            | Builtin BclUInt64 -> "getBytesUInt64"
            | Number UInt32 -> "getBytesUInt32"
            | Number Float32 -> "getBytesSingle"
            | Number Float64 -> "getBytesDouble"
            | x -> failwithf "Unsupported type in BitConverter.GetBytes(): %A" x
        else Naming.lowerFirst i.CompiledName
    Helper.CoreCall("BitConverter", memberName, Boolean, args, i.SignatureArgTypes, ?loc=r) |> Some

let parse isFloat (com: ICompiler) range returnT (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
        // TODO what about Single ?
        let numberModule =
            if isFloat
            then "Double"
            else "Int32"
        match i.CompiledName with
        | "IsNaN" when isFloat ->
            match args with
            | [_someNumber] ->
                Helper.GlobalCall("Number", returnT, args, memb="isNaN", ?loc=range)
                |> Some
            | _ -> None
        // TODO verify that the number is within the Int32/Double/Single range
        | "Parse" | "TryParse" ->
            let hexConst = int System.Globalization.NumberStyles.HexNumber
            match i.CompiledName, args with
            | "Parse", [str] ->
                Helper.CoreCall (
                    numberModule,
                    "parse",
                    returnT,
                    [str; (if isFloat then makeNumConst 10.0 else makeIntConst 10)],
                    [i.SignatureArgTypes.Head; Number Float32])
                |> Some
            | "Parse", [str; Value(Enum(NumberEnum hexConst', _))] ->
                if hexConst' = hexConst then
                    Helper.CoreCall (numberModule, "parse", returnT,
                        [str; (if isFloat then makeNumConst 16.0 else makeIntConst 16)],
                        [i.SignatureArgTypes.Head; Number Float32])
                    |> Some
                else None  (* Todo *)
            // System.Double.Parse(string, IFormatProvider)
            // just ignore the second args (IFormatProvider) and compile
            // to System.Double.Parse(string)
            | "Parse", [str; _ ] ->
                Helper.CoreCall (numberModule, "parse", returnT,
                    [str; (if isFloat then makeNumConst 10.0 else makeIntConst 10)],
                    [i.SignatureArgTypes.Head; Number Float32])
                |> Some
            | "TryParse", [str; defValue] ->
                Helper.CoreCall (numberModule, "tryParse", returnT,
                    [str; (if isFloat then makeNumConst 10.0 else makeIntConst 10); defValue],
                    [i.SignatureArgTypes.Head; Number Float32; i.SignatureArgTypes.Tail.Head])
                |> Some
            | _ ->
                sprintf "%s.%s only accepts a single argument" i.DeclaringEntityFullName i.CompiledName
                |> addErrorAndReturnNull com range |> Some
        | "ToString" ->
            printfn "parse toString args: %A" args
            match args with
            | [Value (StringConstant _) as format] ->
                let format = emitJs range String [format] "'{0:' + $0 + '}'"
                Helper.CoreCall ("String", "format", returnT, [format; thisArg.Value], [format.Type; thisArg.Value.Type])
                |> Some
            | _ -> Helper.CoreCall("Util", "toString", returnT, [thisArg.Value], [thisArg.Value.Type]) |> Some
        | _ -> None

let convert (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let sourceType = firstGenArg com r i.GenericArgs
    match i.CompiledName with
    | "ToSByte" | "ToByte"
    | "ToInt16" | "ToUInt16"
    | "ToInt32" | "ToUInt32"
    | "ToInt64" | "ToUInt64"
        -> toInt true sourceType t args |> Some
    | "ToSingle" | "ToDouble" | "ToDecimal"
        -> toFloat sourceType t args |> Some
    | "ToChar" -> toChar sourceType args |> Some
    | "ToString" -> toString sourceType args |> Some
    | "ToBase64String" | "FromBase64String" ->
        if not(List.isSingle args) then
            sprintf "Convert.%s only accepts one single argument" (Naming.upperFirst i.CompiledName)
            |> addWarning com r
        Helper.CoreCall ("String", (Naming.lowerFirst i.CompiledName), t, args, i.SignatureArgTypes) |> Some
    | _ -> None

let console (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName with
    | "Out" -> None
    | "Write" ->
        addWarning com r "Write will behave as WriteLine"
        log com r t i thisArg args |> Some
    | "WriteLine" -> log com r t i thisArg args |> Some
    | _ -> None

let debug (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName with
    | "Write" ->
        addWarning com r "Write will behave as WriteLine"
        log com r t i thisArg args |> Some
    | "WriteLine" -> log com r t i thisArg args |> Some
    | "Break" -> Debugger |> Some
    | "Assert" ->
        // emit i "if (!$0) { debugger; }" i.args |> Some
        let cond = Operation(UnaryOperation (UnaryNot, args.Head), Boolean, r)
        IfThenElse(cond, Debugger, Value (Null Unit))
        |> Some
    | _ -> None

let dates (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let getTime (e: Expr) =
        Helper.InstanceCall(e, "getTime", t, [])
    let moduleName =
        if i.DeclaringEntityFullName = "System.DateTime"
        then "Date" else "DateOffset"
    match i.CompiledName with
    | ".ctor" ->
        match i.SignatureArgTypes with
        | [] -> Helper.CoreCall (moduleName, "minValue", t, [], []) |> Some
        | (Number _)::_ ->
            match args with
            | ticks::rest ->
                let ms = Helper.CoreCall("Long","ticksToUnixEpochMilliseconds", t, [ticks], [i.SignatureArgTypes.Head])
                Helper.CoreCall(moduleName, "default", t, (ms::rest), t::i.SignatureArgTypes.Tail)
                |> Some
            | _ -> None
        | (DeclaredType(e,[]))::_ when e.FullName = "System.DateTime" ->
            Helper.CoreCall("DateOffset", "fromDate", t, args, i.SignatureArgTypes)
            |> Some
        | _ ->
            let last = List.last args
            match args.Length, last.Type with
            | 7, EnumType(_, "System.DateTimeKind") ->
                let args = (List.take 6 args) @ [makeIntConst 0; last]
                let argTypes = (List.take 6 i.SignatureArgTypes) @ [Number Int32; last.Type]
                Helper.CoreCall("Date", "create", t, args, argTypes) |> Some
            | _ ->
                Helper.CoreCall(moduleName, "create", t, args, i.SignatureArgTypes) |> Some
    | "ToString" ->
        Helper.CoreCall("Date", "toString", t, args, i.SignatureArgTypes, ?thisArg=thisArg) |> Some
    | "Kind" | "Offset" ->
        get r t thisArg.Value (Naming.lowerFirst i.CompiledName) |> Some
    // DateTimeOffset
    | "DateTime" | "LocalDateTime" | "UtcDateTime" as m ->
        let ms = getTime thisArg.Value
        let kind =
            if m = "LocalDateTime" then System.DateTimeKind.Local
            elif m = "UtcDateTime" then System.DateTimeKind.Utc
            else System.DateTimeKind.Unspecified
            |> int |> makeIntConst
        Helper.CoreCall("Date", "default", t, [ms; kind], [ms.Type; kind.Type]) |> Some
    | "FromUnixTimeSeconds"
    | "FromUnixTimeMilliseconds" ->
        let value = Helper.CoreCall("Long", "toNumber", Number Float64, args, i.SignatureArgTypes)
        let value =
            if i.CompiledName = "FromUnixTimeSeconds"
            then makeBinOp r t value (makeIntConst 1000) BinaryMultiply
            else value
        Helper.CoreCall("DateOffset", "default", t, [value; makeIntConst 0], [value.Type; Number Int32]) |> Some
        // ccall i "DateOffset" "default" [value; makeIntConst 0] |> Some
    | "ToUnixTimeSeconds"
    | "ToUnixTimeMilliseconds" ->
        let ms = getTime thisArg.Value
        let args =
            if i.CompiledName = "ToUnixTimeSeconds"
            then [makeBinOp r t ms (makeIntConst 1000) BinaryDivide]
            else [ms]
        Helper.CoreCall("Long", "fromNumber", t, args) |> Some
    // Ticks methods are moved to Long.ts so we don't have to import it to Date.ts
    | "Ticks" | "UtcTicks" | "ToBinary" ->
        let ms = getTime thisArg.Value
        let offset =
            if i.CompiledName = "UtcTicks"
            then makeIntConst 0
            else Helper.CoreCall("Date", "offset", Number Float64, [thisArg.Value], [thisArg.Value.Type])
        Helper.CoreCall(
            "Long", "unixEpochMillisecondsToTicks",
            Number Float64,
            [ms; offset], [ms.Type; offset.Type])
        |> Some
    | "AddTicks" ->
        match thisArg, args with
        | Some c, [ticks] ->
            // TODO: this line in old Replacement.fs is div, but in Long.js there only exists op_Division, should we upgrade the fable-core?
            let ms = Helper.CoreCall("Long", "op_Division", i.SignatureArgTypes.Head, [ticks; makeIntConst 10000], [ticks.Type; Number Int32])
            let ms = Helper.CoreCall("Long", "toNumber", Number Float64, [ms], [ms.Type])
            Helper.CoreCall(moduleName, "addMilliseconds", Number Float64, [c; ms], [c.Type; ms.Type]) |> Some
        | _ -> None
    | _ -> None

let timeSpans (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    // let callee = match i.callee with Some c -> c | None -> i.args.Head
    match i.CompiledName with
    | ".ctor" ->
        Helper.CoreCall("TimeSpan", "create", t, args, i.SignatureArgTypes)
        |> Some
    | "FromMilliseconds" ->
        args.Head |> Some
    | "TotalMilliseconds" ->
        thisArg.Value |> Some
    | _ -> None

let systemEnv (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName with
    | "NewLine" -> Some (makeStrConst "\n")
    | _ -> None

// Initial support, making at least InvariantCulture compile-able
// to be used System.Double.Parse and System.Single.Parse
// see https://github.com/fable-compiler/Fable/pull/1197#issuecomment-348034660
let globalization (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName with
    | "InvariantCulture" ->
        // System.Globalization namespace is not supported by Fable. The value InvariantCulture will be compiled to an empty object literal
        ObjectExpr([], t, None) |> Some
    | _ -> None

let random (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName with
    | ".ctor" -> ObjectExpr ([], t, None) |> Some
    | "Next" ->
        let min, max =
            match args with
            | [] -> makeIntConst 0, makeIntConst System.Int32.MaxValue
            | [max] -> makeIntConst 0, max
            | [min; max] -> min, max
            | _ -> failwith "Unexpected arg count for Random.Next"
        Helper.CoreCall("Util", "randomNext", t, [min; max], [min.Type; max.Type]) |> Some
    | "NextDouble" ->
        Helper.GlobalCall ("Math", t, [], [], memb="random")
        |> Some
    | _ -> None

let cancels (com: ICompiler) r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName with
    | ".ctor" -> Helper.CoreCall("Async", "createCancellationToken", t, args, i.SignatureArgTypes) |> Some
    | "Token" -> thisArg
    | "Cancel" | "CancelAfter" | "IsCancellationRequested" ->
        let args, argTypes = match thisArg with Some c -> c::args, c.Type::i.SignatureArgTypes | None -> args, i.SignatureArgTypes
        Helper.CoreCall("Async", Naming.lowerFirst i.CompiledName, t, args, argTypes) |> Some
    // TODO: Add check so CancellationTokenSource cannot be cancelled after disposed?
    | "Dispose" -> Null Type.Unit |> Value |> Some
    | _ -> None

let activator com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName, thisArg, args with
    | "CreateInstance", None, typRef::args ->
        let info = argInfo None args (Some i.SignatureArgTypes.Tail)
        constructorCall r t info typRef |> Some
    | _ -> None

let regex com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let propInt p callee = getExpr r t callee (makeIntConst p)
    let propStr p callee = getExpr r t callee (makeStrConst p)
    let isGroup =
        match thisArg with
        | Some (ExprType (EntFullName "System.Text.RegularExpressions.Group")) -> true
        | _ -> false
    match i.CompiledName with
    | ".ctor" ->
        // TODO: Use RegexConst if no options have been passed?
        Helper.CoreCall("RegExp", "create", t, args, i.SignatureArgTypes)
        |> Some
    | "Options" ->
        Helper.CoreCall("RegExp", "options", t, [thisArg.Value], [thisArg.Value.Type])
        |> Some
    // Capture
    | "Index" ->
        if not isGroup
        then propStr "index" thisArg.Value |> Some
        else addErrorAndReturnNull com r "Accessing index of Regex groups is not supported"
             |> Some
    | "Value" ->
        if isGroup
        then
            // In JS Regex group values can be undefined, ensure they're empty strings #838
            makeLogOp r thisArg.Value (makeStrConst "") LogicalOr |> Some
        else propInt 0 thisArg.Value |> Some
    | "Length" ->
        if isGroup
        then propStr "length" thisArg.Value |> Some
        else propInt 0 thisArg.Value |> propStr "length" |> Some
    // Group
    | "Success" ->
        makeEqOp r thisArg.Value (Value (Null thisArg.Value.Type)) BinaryUnequal |> Some
    // Match
    | "Groups" -> thisArg.Value |> Some
    // MatchCollection & GroupCollection
    | "Item" ->
        getExpr r t thisArg.Value args.Head |> Some
    | "Count" ->
        propStr "length" thisArg.Value |> Some
    | _ -> None

let enumerable com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match thisArg, i.CompiledName with
    | Some callee, "GetEnumerator" ->
        Helper.CoreCall("Seq", "getEnumerator", t, [callee], [callee.Type]) |> Some
    | _ -> None

let mailbox com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match thisArg with
    | None ->
        match i.CompiledName with
        | ".ctor" -> Helper.CoreCall("MailboxProcessor", "default", t, args, i.SignatureArgTypes) |> Some
        | "Start" -> Helper.CoreCall("MailboxProcessor", "start", t, args, i.SignatureArgTypes) |> Some
        | _ -> None
    | Some callee ->
        match i.CompiledName with
        // `reply` belongs to AsyncReplyChannel
        | "Start" | "Receive" | "PostAndAsyncReply" | "Post" ->
            let memb =
                if i.CompiledName = "Start"
                then "startInstance"
                else Naming.lowerFirst i.CompiledName
            Helper.CoreCall("MailboxProcessor", memb, t, args, i.SignatureArgTypes, thisArg=callee)
            |> Some
        | "Reply" ->
            Helper.InstanceCall(callee, "reply", t, args, i.SignatureArgTypes) |> Some
        | _ -> None

let asyncs com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let asyncMeth meth args argTypes =
        Helper.CoreCall("Async", meth, t, args, argTypes)
        |> Some
    match i.CompiledName with
    | "Start" ->
        // Just add warning, the replacement will actually happen in coreLibPass
        "Async.Start will behave as StartImmediate"
        |> addWarning com r
        None
    | "CancellationToken" ->
        // Make sure cancellationToken is called as a function and not a getter
        asyncMeth "cancellationToken" [] []
    | "Catch" ->
        // `catch` cannot be used as a function name in JS
        asyncMeth "catchAsync" args i.SignatureArgTypes
    | _ -> None

let guids com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName with
    | "NewGuid" ->
        Helper.CoreCall("String", "newGuid", t, [])
        |> Some
    | "Parse" ->
        Helper.CoreCall("String", "validateGuid", t, args, i.SignatureArgTypes) |> Some
    | "TryParse" ->
        Helper.CoreCall("String", "validateGuid", t, [args.Head; makeBoolConst true], [args.Head.Type; Boolean]) |> Some
    | "ToByteArray" ->
        Helper.CoreCall("String", "guidToArray", t, [thisArg.Value], [thisArg.Value.Type]) |> Some
    | ".ctor" ->
        match args with
        | [] -> makeStrConst "00000000-0000-0000-0000-000000000000" |> Some
        | [ExprType (Array _)] ->
            Helper.CoreCall("String", "arrayToGuid", t, args, i.SignatureArgTypes) |> Some
        | [ExprType String as arg] ->
            Helper.CoreCall("String", "validateGuid", t, args, i.SignatureArgTypes) |> Some
        | _ -> None
    | _ -> None

let uris com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName with
    | "UnescapeDataString" ->
        Helper.CoreCall("Util", "unescapeDataString", t, args, i.SignatureArgTypes)
        |> Some
    | "EscapeDataString" ->
        Helper.CoreCall("Util", "escapeDataString", t, args, i.SignatureArgTypes)
        |> Some
    | "EscapeUriString" ->
        Helper.CoreCall("Util", "escapeUriString", t, args, i.SignatureArgTypes)
        |> Some
    | _ -> None

let laziness com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName with
    | ".ctor" | "Create" -> Helper.CoreCall("Util", "Lazy", t, args, i.SignatureArgTypes) |> Some
    | "CreateFromValue" -> Helper.CoreCall("Util", "lazyFromValue", t, args, i.SignatureArgTypes) |> Some
        // coreCall "lazyFromValue" false info.args |> Some
    | "Force" | "Value" | "IsValueCreated" ->
        let callee = thisArg |> Option.defaultWith(fun _ -> args.Head)
        match i.CompiledName with
        | "Force" -> "Value" | another -> another
        |> get r t callee |> Some
    | _ -> None

let controlExtensions com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match i.CompiledName with
    | "AddToObservable" -> Some "add"
    | "SubscribeToObservable" -> Some "subscribe"
    | _ -> None
    |> Option.map (fun meth ->
        let args, argTypes =
            thisArg
            |> Option.map (fun thisArg -> thisArg::args, thisArg.Type::i.SignatureArgTypes)
            |> Option.defaultValue (args, i.SignatureArgTypes)
            |> fun (args, argTypes) -> List.rev args, List.rev argTypes
        Helper.CoreCall("Observable", meth, t, args, argTypes))

let fsharpType methName com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    let hasInterface ifc (typRef: Expr) (typRefType) =
        let proto = Helper.CoreCall("Reflection", "getPrototypeOfType", Any, [typRef], [typRefType], ?loc=typRef.Range)
        Helper.CoreCall("Util", "hasInterface", t, [proto; makeStrConst ifc], [Any; String])
    match methName with
    | "GetRecordFields"
    | "GetExceptionFields" ->
        Helper.CoreCall("Reflection", "getProperties", t, args, i.SignatureArgTypes) |> Some
    | "GetUnionCases"
    | "GetTupleElements"
    | "GetFunctionElements" ->
        Helper.CoreCall("Reflection", Naming.lowerFirst methName, t, args, i.SignatureArgTypes) |> Some
    | "IsUnion" ->
        hasInterface "FSharpUnion" args.Head i.SignatureArgTypes.Head |> Some
    | "IsRecord" ->
        hasInterface "FSharpRecord" args.Head i.SignatureArgTypes.Head |> Some
    | "IsExceptionRepresentation" ->
        hasInterface "FSharpException" args.Head i.SignatureArgTypes.Head |> Some
    | "IsTuple" ->
        Helper.CoreCall("Reflection", "isTupleType", t, args, i.SignatureArgTypes) |> Some
    | "IsFunction" ->
        Helper.CoreCall("Reflection", "isFunctionType", t, args, i.SignatureArgTypes) |> Some
    | _ -> None

let fsharpValue methName com r t (i: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match methName with
    | "GetUnionFields" ->
        Helper.CoreCall("Reflection", "getUnionFields", t, args, i.SignatureArgTypes) |> Some
    | "GetRecordFields"
    | "GetExceptionFields" ->
        Helper.CoreCall("Reflection", "getPropertyValues", t, args, i.SignatureArgTypes) |> Some
    | "GetTupleFields" -> // TODO: Check if it's an array first?
        Some args.Head
    | "GetTupleField" ->
        getExpr r t args.Head args.Tail.Head |> Some
    | "GetRecordField" ->
        match args with
        | [record; propInfo] ->
            let prop = get propInfo.Range String propInfo "name"
            getExpr r t record prop |> Some
        | _ -> None
    | "MakeUnion" ->
        Helper.CoreCall("Reflection", "makeUnion", t, args, i.SignatureArgTypes) |> Some
    | "MakeRecord" ->
        match args with
        | [typ; vals] ->
            let typ = Helper.CoreCall("Util", "getDefinition", Any, [typ], [i.SignatureArgTypes.Head], ?loc=typ.Range)
            let argInfo =
                { ThisArg = None
                  Args = [vals]
                  SignatureArgTypes = Some i.SignatureArgTypes.Tail
                  Spread = SeqSpread
                  IsSiblingConstructorCall = false }
            Operation(Call(ConstructorCall typ, argInfo), t, r) |> Some
        | _ -> None
    | "MakeTuple" ->
        Some args.Head
    | _ -> None

let curryExpr t arity (expr: Expr) =
    Helper.CoreCall("Util", "curry", t, [makeIntConst arity; expr])

let uncurryExpr t arity (expr: Expr) =
    Helper.CoreCall("Util", "uncurry", t, [makeIntConst arity; expr])

let partialApply t arity (fn: Expr) (args: Expr list) =
    let args = NewArray(ArrayValues args, Any) |> Value
    Helper.CoreCall("Util", "partialApply", t, [makeIntConst arity; fn; args])

let tryField returnTyp ownerTyp fieldName =
    match ownerTyp, fieldName with
    | Number Decimal, "Zero" -> makeDecConst 0M |> Some
    | Number Decimal, "One" -> makeDecConst 1M |> Some
    | String, "Emtpy" -> makeStrConst "" |> Some
    | Builtin BclGuid, "Empty" -> makeStrConst "00000000-0000-0000-0000-000000000000" |> Some
    | Builtin BclTimeSpan, "Zero" -> makeIntConst 0 |> Some
    | Builtin BclDateTime, ("MaxValue" | "MinValue") ->
        Helper.CoreCall(coreModFor BclDateTime, Naming.lowerFirst fieldName, returnTyp, []) |> Some
    | Builtin BclDateTimeOffset, ("MaxValue" | "MinValue") ->
        Helper.CoreCall(coreModFor BclDateTimeOffset, Naming.lowerFirst fieldName, returnTyp, []) |> Some
    | _ -> None

let tryCall (com: ICompiler) ctx r t (info: CallInfo) (thisArg: Expr option) (args: Expr list) =
    match info.DeclaringEntityFullName with
    | "System.Math"
    | "Microsoft.FSharp.Core.Operators"
    | "Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators"
    | "Microsoft.FSharp.Core.ExtraTopLevelOperators" -> operators com ctx r t info thisArg args
    | "Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicFunctions"
    | "Microsoft.FSharp.Core.Operators.OperatorIntrinsics" -> intrinsicFunctions com r t info thisArg args
    | "Microsoft.FSharp.Core.LanguagePrimitives.ErrorStrings" -> errorStrings info.CompiledName
    | "Microsoft.FSharp.Core.LanguagePrimitives" -> languagePrimitives com r t info thisArg args
    | "System.Char" -> chars com r t info thisArg args
    | "System.String"
    | "Microsoft.FSharp.Core.StringModule" -> strings com r t info thisArg args
    | "System.Array" -> arrays com r t info thisArg args
    | "Microsoft.FSharp.Collections.ArrayModule" -> arrayModule com r t info thisArg args
    | "Microsoft.FSharp.Collections.FSharpList`1"
    | "Microsoft.FSharp.Collections.ListModule" -> lists com r t info thisArg args
    | "Microsoft.FSharp.Core.CompilerServices.RuntimeHelpers"
    | "Microsoft.FSharp.Collections.SeqModule" -> seqs com r t info thisArg args
    | "System.Collections.Generic.KeyValuePair`2" -> keyValuePairs com r t info thisArg args
    | "System.Collections.Generic.Dictionary`2"
    | "System.Collections.Generic.IDictionary`2" -> dictionaries com r t info thisArg args
    | "System.Collections.Generic.HashSet`1"
    | "System.Collections.Generic.ISet`1" -> hashSets com r t info thisArg args
    | "Microsoft.FSharp.Core.FSharpOption`1"
    | "Microsoft.FSharp.Core.OptionModule" -> options com r t info thisArg args
    | "System.Decimal" -> decimals com r t info thisArg args
    // | "System.Numerics.BigInteger"
    // | "Microsoft.FSharp.Core.NumericLiterals.NumericLiteralI" -> bigint com r t info thisArg args
    | "Microsoft.FSharp.Core.FSharpRef" -> references com r t info thisArg args
    | "Microsoft.FSharp.Core.PrintfModule"
    | Naming.StartsWith "Microsoft.FSharp.Core.PrintfFormat" _ -> fsFormat com r t info thisArg args
    | "Microsoft.FSharp.Core.Operators.Unchecked" -> unchecked com r t info thisArg args
    | "System.Object" -> objects com r t info thisArg args
    | Naming.StartsWith "Fable.Core." _ -> fableCoreLib com r t info thisArg args
    | Naming.EndsWith "Exception" _ -> exceptions com r t info thisArg args
    | "System.Timers.ElapsedEventArgs" -> thisArg // only signalTime is available here
    | Naming.StartsWith "System.Action" _
    | Naming.StartsWith "System.Func" _
    | Naming.StartsWith "Microsoft.FSharp.Core.FSharpFunc" _
    | Naming.StartsWith "Microsoft.FSharp.Core.OptimizedClosures.FSharpFunc" _ -> funcs com r t info thisArg args
    | "System.Enum" -> enums com r t info thisArg args
    | "System.BitConverter" -> bitConvert com r t info thisArg args
    | "System.Int32" -> parse false com r t info thisArg args
    | "System.Single"
    | "System.Double" -> parse true com r t info thisArg args
    | "System.Convert" -> convert com r t info thisArg args
    | "System.Console" -> console com r t info thisArg args
    | "System.Diagnostics.Debug"
    | "System.Diagnostics.Debugger" -> debug com r t info thisArg args
    | "System.DateTime"
    | "System.DateTimeOffset" -> dates com r t info thisArg args
    | "System.TimeSpan" -> timeSpans com r t info thisArg args
    | "System.Environment" -> systemEnv com r t info thisArg args
    | "System.Globalization.CultureInfo" -> globalization com r t info thisArg args
    | "System.Random" -> random com r t info thisArg args
    | "System.Threading.CancellationToken"
    | "System.Threading.CancellationTokenSource" -> cancels com r t info thisArg args
    | "System.Activator" -> activator com r t info thisArg args
    | "System.Text.RegularExpressions.Capture"
    | "System.Text.RegularExpressions.Match"
    | "System.Text.RegularExpressions.Group"
    | "System.Text.RegularExpressions.MatchCollection"
    | "System.Text.RegularExpressions.GroupCollection"
    | "System.Text.RegularExpressions.Regex" -> regex com r t info thisArg args
    | "System.Collections.Generic.IEnumerable`1"
    | "System.Collections.IEnumerable" -> enumerable com r t info thisArg args
    | "Microsoft.FSharp.Collections.FSharpSet`1"
    | "Microsoft.FSharp.Collections.SetModule" -> sets com r t info thisArg args
    | _ -> None
//         | "System.Collections.Generic.Dictionary.KeyCollection"
//         | "System.Collections.Generic.Dictionary.ValueCollection"
//         | "System.Collections.Generic.ICollection" -> collectionsSecondPass com info Seq
//         | "System.Array"
//         | "System.Collections.Generic.List"
//         | "System.Collections.Generic.IList" -> collectionsSecondPass com info Array
//         | "Microsoft.FSharp.Collections.ArrayModule" -> collectionsFirstPass com info Array
//         | "Microsoft.FSharp.Collections.FSharpList"
//         | "Microsoft.FSharp.Collections.ListModule" -> collectionsFirstPass com info List
//         | "Microsoft.FSharp.Collections.FSharpMap"
//         | "Microsoft.FSharp.Collections.MapModule" -> mapAndSets com info
//         | "System.Type" -> types com info // TODO
    | "Microsoft.FSharp.Control.FSharpMailboxProcessor"
    | "Microsoft.FSharp.Control.FSharpAsyncReplyChannel" -> mailbox com r t info thisArg args
    | "Microsoft.FSharp.Control.FSharpAsync" -> asyncs com r t info thisArg args
    | "System.Guid" -> guids com r t info thisArg args
    | "System.Uri" -> uris com r t info thisArg args
    | "System.Lazy" | "Microsoft.FSharp.Control.Lazy"
    | "Microsoft.FSharp.Control.LazyExtensions" -> laziness com r t info thisArg args
    | "Microsoft.FSharp.Control.CommonExtensions" -> controlExtensions com r t info thisArg args
    | "Microsoft.FSharp.Reflection.FSharpType" -> fsharpType info.CompiledName com r t info thisArg args
    | "Microsoft.FSharp.Reflection.FSharpValue" -> fsharpValue info.CompiledName com r t info thisArg args
    | "Microsoft.FSharp.Reflection.FSharpReflectionExtensions" ->
        // In netcore F# Reflection methods become extensions
        // with names like `FSharpType.GetExceptionFields.Static`
        let isFSharpType = info.CompiledName.StartsWith("FSharpType")
        let methName = info.CompiledName |> Naming.extensionMethodName
        if isFSharpType
        then fsharpType methName com r t info thisArg args
        else fsharpValue methName com r t info thisArg args
    | "Microsoft.FSharp.Reflection.UnionCaseInfo"
    | "System.Reflection.PropertyInfo"
    | "System.Reflection.MemberInfo" ->
        match thisArg, info.CompiledName with
        | _, "GetFields" -> Helper.InstanceCall(thisArg.Value, "getUnionFields", t, args) |> Some
        | Some c, "Name" -> Helper.CoreCall("Reflection", "getName", t, [c]) |> Some
        | Some c, ("Tag" | "PropertyType") ->
            let prop =
                if info.CompiledName = "Tag" then "Index" else info.CompiledName
                |> makeStrConst
            getExpr r t c prop |> Some
        | _ -> None
    | _ -> None
