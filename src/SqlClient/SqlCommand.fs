﻿namespace FSharp.Data

open System
open System.Data
open System.Data.SqlClient
open System.Reflection

open FSharp.Data.SqlClient

type ISqlCommand = 
    abstract Execute: parameters: (string * obj)[] -> obj
    abstract AsyncExecute: parameters: (string * obj)[] -> obj
    abstract ToTraceString: parameters: (string * obj)[] -> string
    abstract Raw: SqlCommand with get

type RowMapping = obj[] -> obj

module Seq = 

    let internal toOption source =  
        match source |> Seq.truncate 2 |> Seq.toArray with
        | [||] -> None
        | [| x |] -> Some x
        | _ -> invalidArg "source" "The input sequence contains more than one element."

    let internal ofReader<'TItem> rowMapping (reader : SqlDataReader) = 
        seq {
            use __ = reader
            while reader.Read() do
                let values = Array.zeroCreate reader.FieldCount
                reader.GetValues(values) |> ignore
                yield values |> rowMapping |> unbox<'TItem>
        }

[<RequireQualifiedAccess>]
type ResultRank = 
    | Sequence = 0
    | SingleRow = 1
    | ScalarValue = 2

type Connection =
    | Literal of string
    | NameInConfig of string
    | CreateCommandFunctor of (unit -> SqlCommand)
    | Transaction of SqlTransaction

type RuntimeSqlCommand (connection, commandAlterationFunctor, commandTimeout, sqlStatement, isStoredProcedure, parameters, resultType, rank, rowMapping: RowMapping, itemTypeName) = 

    // use CreateCommandFunctor's functor to create the instance, otherwise simply new a SqlCommand
    let cmd = 
        match connection with
        | CreateCommandFunctor f -> f()
        | _ -> new SqlCommand()
            
    do 
        cmd.CommandText <- sqlStatement
        cmd.CommandType <- if isStoredProcedure then CommandType.StoredProcedure else CommandType.Text

        // we don't want the CommandTimeout to be overwritten when using CreateCommandFunctor
        match connection with
        | CreateCommandFunctor _ -> ()
        | _ -> cmd.CommandTimeout <- commandTimeout
        
    do
        match connection with
        | Literal value -> 
            cmd.Connection <- new SqlConnection(value)
        | NameInConfig name ->
            let connStr = Configuration.GetConnectionStringAtRunTime name
            cmd.Connection <- new SqlConnection(connStr)
        | Transaction t ->
             cmd.Connection <- t.Connection
             cmd.Transaction <- t
        | CreateCommandFunctor _ -> ()
    do
        cmd.Parameters.AddRange(parameters)

    let getReaderBehavior() = 
        seq {
            yield CommandBehavior.SingleResult

            if cmd.Connection.State <> ConnectionState.Open 
            then
                cmd.Connection.Open() 
                yield CommandBehavior.CloseConnection

            if rank = ResultRank.SingleRow then yield CommandBehavior.SingleRow 
        }
        |> Seq.reduce (|||) 

    let execute, asyncExecute = 
        match resultType with
        | ResultType.DataReader -> 
            RuntimeSqlCommand.ExecuteReader >> box, RuntimeSqlCommand.AsyncExecuteReader >> box
        | ResultType.DataTable ->
            RuntimeSqlCommand.ExecuteDataTable >> box, RuntimeSqlCommand.AsyncExecuteDataTable >> box
        | ResultType.Records | ResultType.Tuples ->
            match box rowMapping, itemTypeName with
            | null, itemTypeName when itemTypeName = typeof<unit>.AssemblyQualifiedName ->
                RuntimeSqlCommand.ExecuteNonQuery >> box, RuntimeSqlCommand.AsyncExecuteNonQuery >> box
            | rowMapping, itemTypeName ->
                assert (rowMapping <> null && itemTypeName <> null)
                let itemType = Type.GetType itemTypeName
                
                let executeHandle = 
                    typeof<RuntimeSqlCommand>
                        .GetMethod("ExecuteSeq", BindingFlags.NonPublic ||| BindingFlags.Static)
                        .MakeGenericMethod(itemType)
                        .Invoke(null, [| rank; rowMapping |]) 
                        |> unbox
                
                let asyncExecuteHandle = 
                    typeof<RuntimeSqlCommand>
                        .GetMethod("AsyncExecuteSeq", BindingFlags.NonPublic ||| BindingFlags.Static)
                        .MakeGenericMethod(itemType)
                        .Invoke(null, [| rank; rowMapping |]) 
                        |> unbox
                
                executeHandle >> box, asyncExecuteHandle >> box
        | unexpected -> failwithf "Unexpected ResultType value: %O" unexpected

    member this.CommandTimeout = cmd.CommandTimeout

    member this.AsSqlCommand() = 
        let clone = new SqlCommand(cmd.CommandText, new SqlConnection(cmd.Connection.ConnectionString), CommandType = cmd.CommandType, CommandTimeout = cmd.CommandTimeout)
        clone.Parameters.AddRange <| [| for p in cmd.Parameters -> SqlParameter(p.ParameterName, p.SqlDbType) |]
        RuntimeSqlCommand.AlterWithFunctor(clone, commandAlterationFunctor)
        clone

    interface ISqlCommand with

        member this.Execute parameters = execute(cmd, getReaderBehavior, parameters, commandAlterationFunctor)
        member this.AsyncExecute parameters = asyncExecute(cmd, getReaderBehavior, parameters, commandAlterationFunctor)

        member this.ToTraceString parameters =  
            let clone = this.AsSqlCommand()
            RuntimeSqlCommand.SetParameters(clone, parameters)  
            let parameterDefinition (p : SqlParameter) =
                if p.Size <> 0 then
                    sprintf "%s %A(%d)" p.ParameterName p.SqlDbType p.Size
                else
                    sprintf "%s %A" p.ParameterName p.SqlDbType 
            seq {
                
                yield sprintf "exec sp_executesql N'%s'" (clone.CommandText.Replace("'", "''"))
              
                if clone.Parameters.Count > 0
                then 
                    yield clone.Parameters
                        |> Seq.cast<SqlParameter> 
                        |> Seq.map parameterDefinition
                        |> String.concat ","
                        |> sprintf "N'%s'" 

                if parameters.Length > 0 
                then 
                    yield parameters
                        |> Seq.map(fun (name,value) -> sprintf "%s='%O'" name value) 
                        |> String.concat ","
            } |> String.concat "," //Using string.concat to handle annoying case with no parameters

        member this.Raw = cmd
            
    interface IDisposable with
        member this.Dispose() =
            cmd.Dispose()

    static member internal AlterWithFunctor(cmd, commandAlterationFunctor) =
        match commandAlterationFunctor with
        | Some f -> f cmd
        | None -> ()

    static member internal SetParametersAndPrepare(cmd: SqlCommand, parameters, commandAlterationFunctor) =
      RuntimeSqlCommand.SetParameters(cmd, parameters)
      RuntimeSqlCommand.AlterWithFunctor(cmd, commandAlterationFunctor)

    static member internal SetParameters(cmd: SqlCommand, parameters: (string * obj)[]) = 
        for name, value in parameters do
            
            let p = cmd.Parameters.[name]            

            if value = null 
            then 
                p.Value <- DBNull.Value 
            else
                if not( p.SqlDbType = SqlDbType.Structured)
                then 
                    p.Value <- value
                else
                    let table : DataTable = unbox p.Value
                    table.Rows.Clear()
                    for rowValues in unbox<seq<obj>> value do
                        table.Rows.Add( rowValues :?> obj[]) |> ignore

            if Convert.IsDBNull p.Value 
            then 
                match p.SqlDbType with
                | SqlDbType.NVarChar -> p.Size <- 4000
                | SqlDbType.VarChar -> p.Size <- 8000
                | _ -> ()

//Execute/AsyncExecute versions
    static member internal ExecuteReader(cmd, getReaderBehavior, parameters, commandAlterationFunctor) = 
        RuntimeSqlCommand.SetParametersAndPrepare(cmd, parameters, commandAlterationFunctor)
        cmd.ExecuteReader( getReaderBehavior())

    static member internal AsyncExecuteReader(cmd, getReaderBehavior, parameters, commandAlterationFunctor) = 
        RuntimeSqlCommand.SetParametersAndPrepare(cmd, parameters, commandAlterationFunctor)
        cmd.AsyncExecuteReader( getReaderBehavior())
    
    static member internal ExecuteDataTable(cmd, getReaderBehavior, parameters, commandAlterationFunctor) = 
        use reader = RuntimeSqlCommand.ExecuteReader(cmd, getReaderBehavior, parameters, commandAlterationFunctor)  
        let result = new FSharp.Data.DataTable<DataRow>()
        result.Load(reader)
        result

    static member internal AsyncExecuteDataTable(cmd, getReaderBehavior, parameters, commandAlterationFunctor) = 
        async {
            use! reader = RuntimeSqlCommand.AsyncExecuteReader(cmd, getReaderBehavior, parameters, commandAlterationFunctor) 
            let result = new FSharp.Data.DataTable<DataRow>()
            result.Load(reader)
            return result
        }

    static member internal ExecuteSeq<'TItem> (rank, rowMapper) = fun(cmd, getReaderBehavior, parameters, commandAlterationFunctor) -> 
        let xs = RuntimeSqlCommand.ExecuteReader(cmd, getReaderBehavior, parameters, commandAlterationFunctor) |> Seq.ofReader<'TItem> rowMapper

        if rank = ResultRank.SingleRow 
        then 
            xs |> Seq.toOption |> box
        elif rank = ResultRank.ScalarValue 
        then 
            xs |> Seq.exactlyOne |> box
        else 
            assert (rank = ResultRank.Sequence)
            box xs 
            
    static member internal AsyncExecuteSeq<'TItem> (rank, rowMapper) = fun(cmd, getReaderBehavior, parameters, commandAlterationFunctor) ->
        let xs = 
            async {
                let! reader = RuntimeSqlCommand.AsyncExecuteReader(cmd, getReaderBehavior, parameters, commandAlterationFunctor)
                return reader |> Seq.ofReader<'TItem> rowMapper
            }

        if rank = ResultRank.SingleRow
        then
            async {
                let! xs = xs 
                return xs |> Seq.toOption
            }
            |> box
        elif rank = ResultRank.ScalarValue 
        then 
            async {
                let! xs = xs 
                return xs |> Seq.exactlyOne
            }
            |> box       
        else 
            assert (rank = ResultRank.Sequence)
            box xs 

    static member internal ExecuteNonQuery(cmd, _, parameters, commandAlterationFunctor) = 
        RuntimeSqlCommand.SetParametersAndPrepare(cmd, parameters, commandAlterationFunctor)  
        use openedConnection = cmd.Connection.UseLocally()
        cmd.ExecuteNonQuery() 

    static member internal AsyncExecuteNonQuery(cmd, _, parameters, commandAlterationFunctor) = 
        RuntimeSqlCommand.SetParametersAndPrepare(cmd, parameters, commandAlterationFunctor)  
        async {         
            use openedConnection = cmd.Connection.UseLocally()
            return! cmd.AsyncExecuteNonQuery() 
        }



